using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Wicked.Core.Terminal;

/// <summary>
/// A pty on Windows 10 1809 and later: a pseudo-console fed by two anonymous pipes, the child attached to it, and a job
/// object so that closing the terminal ends everything the shell started.
/// </summary>
internal sealed unsafe class WindowsPty : IPty
{
    private const int ReadSize = 64 * 1024;

    private readonly PtyDataHandler _onData;
    private readonly ConcurrentQueue<byte[]> _input = new();
    private readonly SemaphoreSlim _inputReady = new(0);
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IntPtr _console;
    private readonly IntPtr _inputWrite;
    private readonly IntPtr _outputRead;
    private readonly IntPtr _process;
    private readonly IntPtr _job;
    private int _disposed;
    private int _consoleClosed;

    private WindowsPty(IntPtr console, IntPtr inputWrite, IntPtr outputRead, IntPtr process, IntPtr job, int pid, PtyDataHandler onData)
    {
        _console = console;
        _inputWrite = inputWrite;
        _outputRead = outputRead;
        _process = process;
        _job = job;
        ProcessId = pid;
        _onData = onData;
    }

    public int ProcessId { get; }

    public Task<int> Exited => _exited.Task;

    public static WindowsPty Start(PtyOptions options, PtyDataHandler onData)
    {
        if (!WinNative.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)) throw Failure("CreatePipe");
        if (!WinNative.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0)) throw Failure("CreatePipe");

        var size = new WinNative.Coord { X = (short)options.Columns, Y = (short)options.Rows };
        var hr = WinNative.CreatePseudoConsole(size, inputRead, outputWrite, 0, out var console);
        // The pseudo-console owns these two ends now.
        WinNative.CloseHandle(inputRead);
        WinNative.CloseHandle(outputWrite);
        if (hr != 0) throw new IOException($"CreatePseudoConsole failed (0x{hr:X8})");

        var attributeSize = IntPtr.Zero;
        WinNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
        var attributes = Marshal.AllocHGlobal(attributeSize);
        try
        {
            if (!WinNative.InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeSize)) throw Failure("InitializeProcThreadAttributeList");
            if (!WinNative.UpdateProcThreadAttribute(attributes, 0, (IntPtr)WinNative.ProcThreadAttributePseudoConsole, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw Failure("UpdateProcThreadAttribute");

            var startup = new WinNative.StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.Size = Marshal.SizeOf<WinNative.StartupInfoEx>();

            var commandLine = new StringBuilder(Quote(options.FileName));
            foreach (var argument in options.Arguments) commandLine.Append(' ').Append(Quote(argument));
            var environment = string.Concat(options.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key}={p.Value}\0")) + "\0";

            fixed (char* env = environment)
            {
                var ok = WinNative.CreateProcess(null, commandLine.ToString(), IntPtr.Zero, IntPtr.Zero, false,
                    WinNative.ExtendedStartupInfoPresent | WinNative.CreateUnicodeEnvironment | WinNative.CreateSuspended, (IntPtr)env,
                    string.IsNullOrEmpty(options.WorkingDirectory) ? null : options.WorkingDirectory, ref startup, out var info);
                if (!ok) throw Failure("CreateProcess");

                var job = WinNative.CreateJobObject(IntPtr.Zero, null);
                if (job != IntPtr.Zero)
                {
                    var limits = new WinNative.JobExtendedLimits();
                    limits.Basic.LimitFlags = WinNative.JobKillOnClose;
                    WinNative.SetInformationJobObject(job, WinNative.JobExtendedLimitInformation, ref limits, Marshal.SizeOf<WinNative.JobExtendedLimits>());
                    WinNative.AssignProcessToJobObject(job, info.Process);
                }

                WinNative.ResumeThread(info.Thread);
                WinNative.CloseHandle(info.Thread);
                var pty = new WindowsPty(console, inputWrite, outputRead, info.Process, job, info.ProcessId, onData);
                pty.StartThreads();
                return pty;
            }
        }
        finally
        {
            WinNative.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || Volatile.Read(ref _disposed) != 0) return;
        _input.Enqueue(data.ToArray());
        _inputReady.Release();
    }

    public void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _consoleClosed) == 0 && columns > 0 && rows > 0) WinNative.ResizePseudoConsole(_console, new WinNative.Coord { X = (short)columns, Y = (short)rows });
    }

    public void Kill()
    {
        if (_job != IntPtr.Zero) WinNative.TerminateJobObject(_job, 1);
        else WinNative.TerminateProcess(_process, 1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Kill();
        _inputReady.Release();
    }

    private void StartThreads()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = $"pty read {ProcessId}" }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = $"pty write {ProcessId}" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = $"pty wait {ProcessId}" }.Start();
    }

    private void ReadLoop()
    {
        try
        {
            while (true)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(ReadSize);
                uint n;
                bool ok;
                fixed (byte* p = buffer) ok = WinNative.ReadFile(_outputRead, p, ReadSize, out n, IntPtr.Zero);
                if (!ok || n == 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    break;
                }

                _onData(buffer, (int)n);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Terminal read ended: {e.Message}");
        }
    }

    private void WriteLoop()
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                _inputReady.Wait();
                while (_input.TryDequeue(out var data))
                {
                    var offset = 0;
                    while (offset < data.Length)
                    {
                        uint written;
                        bool ok;
                        fixed (byte* p = &data[offset]) ok = WinNative.WriteFile(_inputWrite, p, (uint)(data.Length - offset), out written, IntPtr.Zero);
                        if (!ok) return;
                        offset += (int)written;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Terminal write ended: {e.Message}");
        }
    }

    private void WaitLoop()
    {
        WinNative.WaitForSingleObject(_process, uint.MaxValue);
        WinNative.GetExitCodeProcess(_process, out var code);
        // Closing the pseudo-console flushes its last output and ends the read pipe; the read thread then finishes.
        if (Interlocked.Exchange(ref _consoleClosed, 1) == 0) WinNative.ClosePseudoConsole(_console);
        Thread.Sleep(100);
        WinNative.CloseHandle(_inputWrite);
        _exited.TrySetResult(unchecked((int)code));
    }

    private static IOException Failure(string what) => new($"{what} failed (error {Marshal.GetLastWin32Error()})");

    /// <summary>One argument quoted the way CreateProcess's command-line parsing reads it back.</summary>
    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0) return argument;
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"') result.Append('\\', backslashes * 2 + 1).Append('"');
            else result.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }

        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }
}

internal static unsafe partial class WinNative
{
    public const uint ExtendedStartupInfoPresent = 0x00080000;
    public const uint CreateUnicodeEnvironment = 0x00000400;
    public const uint CreateSuspended = 0x00000004;
    public const int ProcThreadAttributePseudoConsole = 0x00020016;
    public const uint JobKillOnClose = 0x2000;
    public const int JobExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved2Ptr;
        public IntPtr StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobBasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobExtendedLimits
    {
        public JobBasicLimits Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, uint size);

    [DllImport("kernel32.dll")]
    public static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    public static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll")]
    public static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    public static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcess(string? application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobExtendedLimits info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll")]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    public static extern int ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(IntPtr file, byte* buffer, uint count, out uint read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(IntPtr file, byte* buffer, uint count, out uint written, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);
}
