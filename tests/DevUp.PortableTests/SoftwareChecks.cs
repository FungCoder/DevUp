using DevUp.Core;
using DevUp.Models;
using DevUp.Services;

internal static class SoftwareChecks
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var progress = new WingetDownloadProgress();
        Check(progress.GetProgress(0, false).Detail == "速度 —" && !progress.HasDownloadData,
            "没有下载计数时编造了速度");
        Check(!progress.Observe("Downloading https://example.invalid/setup.exe", 0), "下载地址被当成字节计数");
        Check(!progress.Observe("200 MB / 100 MB", 0), "无效下载计数未被拒绝");
        Check(progress.Observe("0 MB / 100 MB", 0), "首个下载计数未识别");
        Check(progress.GetProgress(0, false).Percent == 0, "首个有效计数未显示下载进度");
        Check(progress.Observe("10 MB / 100 MB", 1000), "实际下载计数未识别");
        Check(progress.GetProgress(1000, false).Detail!.Contains("10 MB/s") &&
            progress.GetProgress(1000, false).Percent == 10, "实际下载速度或百分比计算错误");
        progress.Observe("10 MB / 100 MB", 2000);
        Check(progress.GetProgress(2000, false).Detail!.Contains("10 MB/s"), "重复计数错误刷新了采样时间");
        Check(progress.GetProgress(7000, true).Detail!.Contains("10 MB/s") &&
            progress.GetProgress(7000, true).DetailHint!.Contains("最近"), "延迟回调被错误判断为零速度");
        progress.Observe("1,5 MiB / 100 MiB", 8000);
        Check(progress.GetProgress(8000, false).Detail == "速度 —", "下载重启后仍沿用旧速度");
        progress.Observe("2,5 MiB / 100 MiB", 9000);
        Check(progress.GetProgress(9000, false).Detail!.Contains("1 MB/s"), "本地化数字或 MiB 单位解析错误");
        progress.Reset();
        Check(!progress.HasDownloadData && progress.GetProgress(10000, false).Percent == -1,
            "新一轮下载沿用了上一安装包的数据");
        progress.Observe("20 MB / 80 MB", 11000);
        Check(progress.GetProgress(11000, false).Percent == 25, "新安装包的总量没有生效");
        var complete = progress.GetProgress(12000, false, completed: true);
        Check(complete.Percent == 100 && complete.Metric == "80 MB / 80 MB", "校验通过后丢失完整下载量");
        progress.Reset();
        progress.Observe("512 KB / 1 MB", 13000);
        Check(progress.GetProgress(13000, false).Percent == 50, "不同字节单位的比例错误");
        progress.Observe("1 MB / 1 MB", 14000);
        Check(progress.GetProgress(14000, false).Percent == 99, "未确认完成前提前结束进度");
        progress.Reset();
        Check(progress.Observe("    12 MB", 15000), "未知总量时没有读取实际下载量");
        progress.Observe("    14 MB", 16000);
        Check(progress.GetProgress(16000, false).Detail!.Contains("2 MB/s") &&
            progress.GetProgress(16000, false).Percent == -1, "未知总量时速度或不确定进度错误");
        progress.Reset(17000);
        progress.Observe("1 MB / 100 MB", 18000);
        Check(progress.GetProgress(18000, false).Detail!.Contains("1 MB/s"), "没有利用真实下载起点计算首个采样速度");


        var qq = new SoftwareItem { DisplayName = "QQ", DisplayVersion = "9.9.33.52230" };
        var music = new SoftwareItem { DisplayName = "QQ音乐", DisplayVersion = "22.76" };
        var docker = new SoftwareItem { DisplayName = "Docker Desktop", DisplayVersion = "4.96.1" };
        var inventory = new[] { qq, music, docker };
        var results = new[]
        {
            new SoftwareUpdateItem { Name = "QQ", Id = "Tencent.QQ.NT", InstalledVersion = qq.DisplayVersion },
            new SoftwareUpdateItem { Name = "QQ音乐", Id = "Tencent.QQMusic", InstalledVersion = music.DisplayVersion },
            new SoftwareUpdateItem { Name = "Docker Desktop", Id = "Docker.DockerDesktop", InstalledVersion = docker.DisplayVersion, AvailableVersion = "4.97.0" },
            new SoftwareUpdateItem { Name = "Missing local row", Id = "Vendor.Current", InstalledVersion = "1.0" },
            new SoftwareUpdateItem { Name = "Missing update row", Id = "Vendor.Upgrade", InstalledVersion = "1.0", AvailableVersion = "2.0" }
        };
        var extras = SoftwareService.Correlate(inventory, results);
        Check(qq.WingetId == "Tencent.QQ.NT" && !qq.HasUpdate, "无更新的 QQ 没有正确关联 NT 包");
        Check(music.HasWingetMatch && !music.HasUpdate, "无更新的 QQ 音乐被标为未匹配或可更新");
        Check(docker.HasUpdate && docker.AvailableVersion == "4.97.0", "现有更新关联被破坏");
        Check(extras.Count == 1 && extras[0].WingetId == "Vendor.Upgrade", "无更新的未关联条目产生了重复清单");
        var wrongVersion = new[] { new SoftwareItem { DisplayName = "QQ", DisplayVersion = "8.0" } };
        SoftwareService.Correlate(wrongVersion, new[] { results[0] });
        Check(!wrongVersion[0].HasWingetMatch, "不同已装版本被错误关联");
        Console.WriteLine($"软件匹配与下载速度检查通过：{checks} 项。");
    }

}
