using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using DevUp.Models;
using DevUp.Services;
using DevUp.Views;

namespace DevUp.Core;

/// <summary>用户主动取消（例如在 UAC 对话框点了“否”）。这类情况不是错误，只提示即可。</summary>
public sealed class UserCancelledException : Exception
{
    public UserCancelledException(string message) : base(message) { }
}

/// <summary>全局共享状态与服务（页面之间共用一份）。</summary>
public static class Ctx
{
    public static ScanState State { get; } = new();

    /// <summary>用户设置（备份 / 下载目录等）。</summary>
    public static AppSettings Settings { get; } = AppSettings.Load();

    public static DeviceService Devices { get; } = new();
    public static DriverStoreService DriverStore { get; } = new();
    public static DriverUpdateService DriverUpdates { get; } = new();
    public static SoftwareService Software { get; } = new();
    public static BackupService Backups { get; } = new();
    public static NpmService Npm { get; } = new();

    /// <summary>系统概览信息（单例，属性变化会通知界面）。</summary>
    public static SystemInfo Info { get; } = new();

    /// <summary>统一执行入口：负责忙碌态、异常兜底与状态文字。</summary>
    public static async Task RunAsync(string status, Func<Task> action)
    {
        if (State.IsBusy)
        {
            Ui.Warn("正在执行其他操作", "请等待当前任务完成后再试。");
            return;
        }

        State.IsBusy = true;
        State.StatusText = status;
        Ui.Log(status);

        try
        {
            await action();
            State.StatusText = "完成 · " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (UserCancelledException ex)
        {
            State.StatusText = "已取消";
            Ui.Log(status + " 已取消：" + ex.Message, "WARN");
            Ui.Info("已取消", ex.Message);
        }
        catch (Exception ex)
        {
            State.StatusText = "失败";
            Ui.Log($"{status} 失败: {ex}", "ERROR");
            Ui.Error("操作失败", ex.Message);
        }
        finally
        {
            State.IsBusy = false;
        }
    }

    /// <summary>运行可取消的扫描，并在超时或失败时给出明确结果。</summary>
    public static async Task<bool> RunScanAsync(
        string status, Func<IProgress<ProgressInfo>, CancellationToken, Task> action,
        TimeSpan? timeout = null)
    {
        if (State.IsBusy)
        {
            Ui.Warn("正在执行其他操作", "请等待当前任务完成后再试。");
            return false;
        }

        State.IsBusy = true;
        await Task.Yield();
        try
        {
            while (true)
            {
                State.StatusText = status;
                Ui.Log(status);
                try
                {
                    var completed = ProgressDialog.Run("扫描与检查", status,
                        async (progress, ct) =>
                        {
                            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
                            progress.Report(new ProgressInfo
                            {
                                Status = status, Detail = "可随时取消"
                            });
                            try
                            {
                                await action(progress, limit.Token);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                throw new TimeoutException(
                                    $"{status}超时，已停止。请检查网络或系统服务后重试。");
                            }
                        });
                    State.StatusText = completed
                        ? "完成 · " + DateTime.Now.ToString("HH:mm:ss") : "已取消";
                    if (!completed) Ui.Log(status + " 已取消", "WARN");
                    return completed;
                }
                catch (UserCancelledException ex)
                {
                    State.StatusText = "已取消";
                    Ui.Info("已取消", ex.Message);
                    return false;
                }
                catch (Exception ex)
                {
                    State.StatusText = "失败";
                    Ui.Log($"{status} 失败: {ex}", "ERROR");
                    if (!Ui.Confirm("扫描或检查失败",
                        ex.Message + "\n\n是否重试本次操作？")) return false;
                }
            }
        }
        finally
        {
            State.IsBusy = false;
        }
    }

    public static async Task LoadSystemInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await PsBridge.RunAsync("systemInfo", null, false, ct);
            if (json.TryGetProperty("data", out var data) && data.TryGetProperty("system", out var s))
            {
                Info.OsName = DriverUpdateService.Str(s, "osName");
                Info.OsVersion = DriverUpdateService.Str(s, "osVersion");
                Info.OsBuild = DriverUpdateService.Str(s, "osBuild");
                Info.Arch = DriverUpdateService.Str(s, "arch");
                Info.InstallDate = DriverUpdateService.Str(s, "installDate");
                Info.Manufacturer = DriverUpdateService.Str(s, "manufacturer");
                Info.Model = DriverUpdateService.Str(s, "model");
                Info.MachineName = DriverUpdateService.Str(s, "machineName");
                Info.Cpu = DriverUpdateService.Str(s, "cpu");
                Info.Cores = s.TryGetProperty("cores", out var cores) && cores.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? $"{cores.GetInt32()} 个逻辑处理器"
                    : "";
                Info.Ram = DriverUpdateService.Str(s, "ram");
                Info.BiosVersion = DriverUpdateService.Str(s, "biosVersion");
                Info.BiosDate = DriverUpdateService.Str(s, "biosDate");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Ui.Log("读取系统信息失败: " + ex.Message, "WARN");
        }
    }
}

public sealed class SystemInfo : INotifyPropertyChanged
{
    private string _osName = "", _osVersion = "", _osBuild = "", _arch = "", _installDate = "",
                    _manufacturer = "", _model = "", _machineName = "", _cpu = "", _cores = "", _ram = "",
                    _biosVersion = "", _biosDate = "";

    public string OsName { get => _osName; set => Set(ref _osName, value, nameof(OsName)); }
    public string OsVersion { get => _osVersion; set { Set(ref _osVersion, value, nameof(OsVersion)); Raise(nameof(OsLine)); } }
    public string OsBuild { get => _osBuild; set { Set(ref _osBuild, value, nameof(OsBuild)); Raise(nameof(OsLine)); } }

    /// <summary>“版本 25H2（内部版本 26200.7019）”这样的整行描述。</summary>
    public string OsLine
    {
        get
        {
            if (string.IsNullOrWhiteSpace(OsVersion) && string.IsNullOrWhiteSpace(OsBuild)) return "";
            if (string.IsNullOrWhiteSpace(OsVersion)) return $"内部版本 {OsBuild}";
            if (string.IsNullOrWhiteSpace(OsBuild)) return $"版本 {OsVersion}";
            return $"版本 {OsVersion}（内部版本 {OsBuild}）";
        }
    }

    public string Arch { get => _arch; set => Set(ref _arch, value, nameof(Arch)); }
    public string InstallDate { get => _installDate; set => Set(ref _installDate, value, nameof(InstallDate)); }
    public string Manufacturer { get => _manufacturer; set => Set(ref _manufacturer, value, nameof(Manufacturer)); }
    public string Model { get => _model; set => Set(ref _model, value, nameof(Model)); }
    public string MachineName { get => _machineName; set => Set(ref _machineName, value, nameof(MachineName)); }
    public string Cpu { get => _cpu; set => Set(ref _cpu, value, nameof(Cpu)); }
    public string Cores { get => _cores; set => Set(ref _cores, value, nameof(Cores)); }
    public string Ram { get => _ram; set => Set(ref _ram, value, nameof(Ram)); }
    public string BiosVersion { get => _biosVersion; set => Set(ref _biosVersion, value, nameof(BiosVersion)); }
    public string BiosDate { get => _biosDate; set => Set(ref _biosDate, value, nameof(BiosDate)); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, string propertyName)
    {
        if (field == value) return;
        field = value;
        Raise(propertyName);
    }

    private void Raise(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
