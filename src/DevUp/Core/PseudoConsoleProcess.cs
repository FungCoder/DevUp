using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DevUp.Core;

/// <summary>以隐藏伪终端运行命令，保留被普通 stdout 重定向隐藏的实时进度。</summary>
internal sealed class PseudoConsoleProcess : IDisposable
{
    private const int Width = 180, Height = 60;
    private IntPtr _console;
    private FileStream? _input, _output;
    private Task _reader = Task.CompletedTask;
    private Process? _process;
    private Exception? _outputError;
    private int _disposed;
    public Process Process => _process ?? throw new InvalidOperationException("子进程尚未启动。");

    public static PseudoConsoleProcess Start(string executable, IEnumerable<string> arguments, Action<string> onLine)
    {
        var session = new PseudoConsoleProcess();
        IntPtr inputRead = IntPtr.Zero, inputWrite = IntPtr.Zero, outputRead = IntPtr.Zero, outputWrite = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        var initialized = false;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) ||
                !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(new Coord(Width, Height), inputRead, outputWrite, 0, out session._console));
            CloseHandle(inputRead);
            inputRead = IntPtr.Zero;
            CloseHandle(outputWrite);
            outputWrite = IntPtr.Zero;
            session._input = new FileStream(new SafeFileHandle(inputWrite, true), FileAccess.Write);
            inputWrite = IntPtr.Zero;
            session._output = new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read);
            outputRead = IntPtr.Zero;
            session._reader = Task.Run(() =>
            {
                using var reader = new StreamReader(session._output, Encoding.UTF8, false, 4096, leaveOpen: true);
                var terminal = new TerminalOutput(Width, Height, onLine);
                var buffer = new char[4096];
                try
                {
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (session._outputError is not null) continue;
                        try { terminal.Append(buffer.AsSpan(0, read)); }
                        catch (Exception ex) { Interlocked.CompareExchange(ref session._outputError, ex, null); }
                    }
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 109 or 232) { }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref session._outputError, ex, null);
                    session._output.Dispose();
                }
            });

            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            initialized = true;
            if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20016), session._console,
                    (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    StandardInput = new IntPtr(-1), StandardOutput = new IntPtr(-1), StandardError = new IntPtr(-1)
                },
                AttributeList = attributes
            };
            var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(QuoteArgument)));
            // 先挂起，取得 .NET Process 句柄后再运行，避免短命令结束导致 PID 竞态。
            if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, 0x80000 | 0x4,
                    IntPtr.Zero, PortablePaths.Root, ref startup, out var info)) throw new Win32Exception();
            try
            {
                session._process = System.Diagnostics.Process.GetProcessById(info.ProcessId);
                _ = session._process.Handle;
                if (ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception();
            }
            catch { TerminateProcess(info.Process, 1); throw; }
            finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            foreach (var handle in new[] { inputRead, inputWrite, outputRead, outputWrite })
                if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    public async Task CompleteOutputAsync()
    {
        var console = Interlocked.Exchange(ref _console, IntPtr.Zero);
        if (console != IntPtr.Zero) ClosePseudoConsole(console);
        await _reader.ConfigureAwait(false);
        ThrowIfOutputFailed();
    }

    public void ThrowIfOutputFailed()
    {
        if (Volatile.Read(ref _outputError) is { } error)
            throw new IOException("读取 winget 实时进度失败。", error);
    }

    public async Task StopAsync()
    {
        if (Process.HasExited) return;
        try { _input?.WriteByte(3); _input?.Flush(); } catch (IOException) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Process.WaitForExitAsync(killed.Token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_process is not null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        finally
        {
            try { CompleteOutputAsync().GetAwaiter().GetResult(); }
            finally { _input?.Dispose(); _output?.Dispose(); _process?.Dispose(); }
        }
    }

    internal static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"'))
            return value;
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(character);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y) { public readonly short X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, uint size);
    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
        nuint size, IntPtr previousValue, IntPtr returnSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
