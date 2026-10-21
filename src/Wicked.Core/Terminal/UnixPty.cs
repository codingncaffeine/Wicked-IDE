using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Wicked.Core.Terminal;

/// <summary>
/// A pty on Linux and macOS. The child is started with <c>posix_spawn</c> as a session leader whose standard streams are
/// the pty's slave side, because managed code cannot run between a <c>fork</c> and an <c>exec</c>. Opening the slave in
/// the new session (without O_NOCTTY) makes it the child's controlling terminal, which is what job control needs.
/// </summary>
internal sealed class UnixPty : IPty
{
    private const int ReadSize = 64 * 1024;
    private static readonly Lock PtsGate = new();

    private readonly int _master;
    private readonly PtyDataHandler _onData;
    private readonly ConcurrentQueue<byte[]> _input = new();
    private readonly SemaphoreSlim _inputReady = new(0);
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private int _fdUsers = 2;
    private int _disposed;
    private volatile bool _childGone;
    private int _exitCode;

    private UnixPty(int master, int pid, PtyDataHandler onData)
    {
        _master = master;
        ProcessId = pid;
        _onData = onData;
    }

    public int ProcessId { get; }

    public Task<int> Exited => _exited.Task;

    public static UnixPty Start(PtyOptions options, PtyDataHandler onData)
    {
        var master = UnixNative.posix_openpt(UnixNative.ORdwr | UnixNative.ONoctty);
        if (master < 0) throw new IOException($"posix_openpt failed (errno {Marshal.GetLastPInvokeError()})");
        try
        {
            if (UnixNative.grantpt(master) != 0 || UnixNative.unlockpt(master) != 0) throw new IOException($"grantpt/unlockpt failed (errno {Marshal.GetLastPInvokeError()})");
            string slave;
            lock (PtsGate) slave = Marshal.PtrToStringUTF8(UnixNative.ptsname(master)) ?? throw new IOException("ptsname failed");
            UnixNative.fcntl(master, UnixNative.FSetfd, UnixNative.FdCloexec);
            var flags = UnixNative.fcntl(master, UnixNative.FGetfl, 0);
            UnixNative.fcntl(master, UnixNative.FSetfl, flags | UnixNative.ONonblock);
            SetSize(master, options.Columns, options.Rows);

            var pid = Spawn(options, slave);
            var pty = new UnixPty(master, pid, onData);
            pty.StartThreads();
            return pty;
        }
        catch
        {
            UnixNative.close(master);
            throw;
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
        if (Volatile.Read(ref _disposed) == 0 && columns > 0 && rows > 0) SetSize(_master, columns, rows);
    }

    public void Kill()
    {
        if (_childGone) return;
        // The whole group: the shell, what it started, and anything that group started in turn.
        UnixNative.kill(-ProcessId, UnixNative.SigHup);
        UnixNative.kill(-ProcessId, UnixNative.SigCont);
        _ = Task.Delay(400).ContinueWith(_ =>
        {
            if (!_childGone) UnixNative.kill(-ProcessId, UnixNative.SigKill);
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Kill();
        _stop.Cancel();
        _inputReady.Release();
    }

    private void StartThreads()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = $"pty read {ProcessId}" }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = $"pty write {ProcessId}" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = $"pty wait {ProcessId}" }.Start();
    }

    private void ReleaseFd()
    {
        if (Interlocked.Decrement(ref _fdUsers) == 0) UnixNative.close(_master);
    }

    private unsafe void ReadLoop()
    {
        var poll = new UnixNative.PollFd { Fd = _master, Events = UnixNative.PollIn };
        var quiet = 0;
        try
        {
            while (true)
            {
                var ready = UnixNative.poll(&poll, 1, 50);
                if (ready < 0 && Marshal.GetLastPInvokeError() != UnixNative.Eintr) break;
                if (ready <= 0)
                {
                    // Nothing for 50 ms. Once the child is gone and the pipe has stayed quiet, a background program still holding the slave open must not keep the tab alive.
                    if (_childGone && ++quiet >= 4) break;
                    if (_stop.IsCancellationRequested && _childGone) break;
                    continue;
                }

                quiet = 0;
                var buffer = ArrayPool<byte>.Shared.Rent(ReadSize);
                long n;
                fixed (byte* p = buffer) n = UnixNative.read(_master, p, ReadSize);
                if (n > 0)
                {
                    _onData(buffer, (int)n);
                    continue;
                }

                ArrayPool<byte>.Shared.Return(buffer);
                if (n == 0) break;
                var errno = Marshal.GetLastPInvokeError();
                if (errno == UnixNative.Eintr || errno == UnixNative.Eagain) continue;
                // EIO is how Linux says every slave end is closed; anything else is just as final.
                break;
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Terminal read ended: {e.Message}");
        }
        finally
        {
            ReleaseFd();
            // The exit code comes from the wait thread; the output has all been handed over by now.
            _ = FinishAsync();
        }
    }

    private async Task FinishAsync()
    {
        for (var i = 0; i < 100 && !_childGone; i++) await Task.Delay(20).ConfigureAwait(false);
        if (!_childGone)
        {
            // The pipe closed but the child lives (it closed its terminal): the tab ends with it.
            Kill();
            for (var i = 0; i < 50 && !_childGone; i++) await Task.Delay(20).ConfigureAwait(false);
        }

        _exited.TrySetResult(_exitCode);
    }

    private unsafe void WriteLoop()
    {
        var poll = new UnixNative.PollFd { Fd = _master, Events = UnixNative.PollOut };
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                _inputReady.Wait();
                while (_input.TryDequeue(out var data))
                {
                    var offset = 0;
                    while (offset < data.Length && !_stop.IsCancellationRequested)
                    {
                        long n;
                        fixed (byte* p = &data[offset]) n = UnixNative.write(_master, p, data.Length - offset);
                        if (n > 0)
                        {
                            offset += (int)n;
                            continue;
                        }

                        var errno = Marshal.GetLastPInvokeError();
                        if (errno == UnixNative.Eagain) UnixNative.poll(&poll, 1, 100);
                        else if (errno != UnixNative.Eintr) return;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Terminal write ended: {e.Message}");
        }
        finally
        {
            ReleaseFd();
        }
    }

    private void WaitLoop()
    {
        int status;
        while (true)
        {
            var result = UnixNative.waitpid(ProcessId, out status, 0);
            if (result == ProcessId) break;
            if (result < 0 && Marshal.GetLastPInvokeError() == UnixNative.Eintr) continue;
            status = 0;
            break;
        }

        // A normal exit carries its code in the second byte; death by a signal is reported the way shells do, 128 + the signal.
        _exitCode = (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
        _childGone = true;
    }

    private static void SetSize(int master, int columns, int rows)
    {
        var size = new UnixNative.WinSize { Rows = (ushort)rows, Columns = (ushort)columns };
        UnixNative.ioctl(master, UnixNative.TiocSwinsz, ref size);
    }

    private static int Spawn(PtyOptions options, string slave)
    {
        var argv = new List<string> { options.FileName };
        argv.AddRange(options.Arguments);
        var envp = options.Environment.Select(pair => $"{pair.Key}={pair.Value}").ToList();

        var allocations = new List<IntPtr>();
        var attr = Alloc(allocations, 1024);
        var actions = Alloc(allocations, 1024);
        var attrReady = false;
        var actionsReady = false;
        try
        {
            Check(UnixNative.posix_spawnattr_init(attr), "posix_spawnattr_init");
            attrReady = true;
            Check(UnixNative.posix_spawn_file_actions_init(actions), "posix_spawn_file_actions_init");
            actionsReady = true;

            // A fresh session, default signal handlers (the runtime ignores some, and the shell must not inherit that), nothing blocked.
            var all = Alloc(allocations, 256);
            var none = Alloc(allocations, 256);
            UnixNative.sigfillset(all);
            UnixNative.sigemptyset(none);
            Check(UnixNative.posix_spawnattr_setsigdefault(attr, all), "setsigdefault");
            Check(UnixNative.posix_spawnattr_setsigmask(attr, none), "setsigmask");
            Check(UnixNative.posix_spawnattr_setflags(attr, (short)(UnixNative.SpawnSetSid | UnixNative.SpawnSetSigDef | UnixNative.SpawnSetSigMask)), "setflags");

            Check(UnixNative.posix_spawn_file_actions_addopen(actions, 0, slave, UnixNative.ORdwr, 0), "addopen");
            Check(UnixNative.posix_spawn_file_actions_adddup2(actions, 0, 1), "adddup2");
            Check(UnixNative.posix_spawn_file_actions_adddup2(actions, 0, 2), "adddup2");
            TryCloseFrom(actions);

            var program = options.FileName;
            if (!string.IsNullOrEmpty(options.WorkingDirectory))
            {
                if (!TryChdir(actions, options.WorkingDirectory))
                {
                    // No addchdir on this libc: a shell changes directory and then becomes the program.
                    argv = ["/bin/sh", "-c", "cd \"$0\" && exec \"$@\"", options.WorkingDirectory, .. argv];
                    program = "/bin/sh";
                }
            }

            var argvPtr = AllocStrings(allocations, argv);
            var envpPtr = AllocStrings(allocations, envp);
            var rc = UnixNative.posix_spawnp(out var pid, program, actions, attr, argvPtr, envpPtr);
            if (rc != 0) throw new IOException($"could not start {options.FileName}: {Marshal.GetPInvokeErrorMessage(rc)}");
            return pid;
        }
        finally
        {
            if (actionsReady) UnixNative.posix_spawn_file_actions_destroy(actions);
            if (attrReady) UnixNative.posix_spawnattr_destroy(attr);
            foreach (var p in allocations) Marshal.FreeHGlobal(p);
        }
    }

    private static void TryCloseFrom(IntPtr actions)
    {
        try
        {
            UnixNative.posix_spawn_file_actions_addclosefrom_np(actions, 3);
        }
        catch (EntryPointNotFoundException)
        {
            // Older libc: the runtime opens its descriptors close-on-exec, so nothing else leaks into the child.
        }
    }

    private static bool TryChdir(IntPtr actions, string directory)
    {
        try
        {
            return UnixNative.posix_spawn_file_actions_addchdir_np(actions, directory) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw new IOException($"{what} failed: {Marshal.GetPInvokeErrorMessage(rc)}");
    }

    private static unsafe IntPtr Alloc(List<IntPtr> allocations, int size)
    {
        var p = Marshal.AllocHGlobal(size);
        new Span<byte>((void*)p, size).Clear();
        allocations.Add(p);
        return p;
    }

    private static IntPtr AllocStrings(List<IntPtr> allocations, List<string> strings)
    {
        var table = Alloc(allocations, (strings.Count + 1) * IntPtr.Size);
        for (var i = 0; i < strings.Count; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(strings[i]);
            var text = Alloc(allocations, bytes.Length + 1);
            Marshal.Copy(bytes, 0, text, bytes.Length);
            Marshal.WriteIntPtr(table, i * IntPtr.Size, text);
        }

        Marshal.WriteIntPtr(table, strings.Count * IntPtr.Size, IntPtr.Zero);
        return table;
    }

}

internal static unsafe partial class UnixNative
{
    private static readonly bool Mac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public const int PollIn = 0x1;
    public const int PollOut = 0x4;
    public const int Eintr = 4;
    public const int ORdwr = 2;
    public const int FSetfd = 2;
    public const int FGetfl = 3;
    public const int FSetfl = 4;
    public const int FdCloexec = 1;
    public const int SigHup = 1;
    public const int SigKill = 9;
    public const int SigCont = 18;

    // The numbers that differ between Linux and macOS.
    public static readonly int Eagain = Mac ? 35 : 11;
    public static readonly int ONoctty = Mac ? 0x20000 : 0x100;
    public static readonly int ONonblock = Mac ? 0x4 : 0x800;
    public static readonly ulong TiocSwinsz = Mac ? 0x80087467UL : 0x5414UL;
    public static readonly int SpawnSetSigDef = 0x04;
    public static readonly int SpawnSetSigMask = 0x08;
    public static readonly int SpawnSetSid = Mac ? 0x400 : 0x80;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort XPixel;
        public ushort YPixel;
    }

    [LibraryImport("libc", SetLastError = true)]
    public static partial int posix_openpt(int flags);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int grantpt(int fd);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int unlockpt(int fd);

    [LibraryImport("libc", SetLastError = true)]
    public static partial IntPtr ptsname(int fd);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int fcntl(int fd, int command, int argument);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int ioctl(int fd, ulong request, ref WinSize size);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int poll(PollFd* fds, ulong count, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    public static partial nint read(int fd, byte* buffer, nint count);

    [LibraryImport("libc", SetLastError = true)]
    public static partial nint write(int fd, byte* buffer, nint count);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int kill(int pid, int signal);

    [LibraryImport("libc")]
    public static partial int sigfillset(IntPtr set);

    [LibraryImport("libc")]
    public static partial int sigemptyset(IntPtr set);

    [LibraryImport("libc")]
    public static partial int posix_spawnattr_init(IntPtr attr);

    [LibraryImport("libc")]
    public static partial int posix_spawnattr_destroy(IntPtr attr);

    [LibraryImport("libc")]
    public static partial int posix_spawnattr_setflags(IntPtr attr, short flags);

    [LibraryImport("libc")]
    public static partial int posix_spawnattr_setsigdefault(IntPtr attr, IntPtr set);

    [LibraryImport("libc")]
    public static partial int posix_spawnattr_setsigmask(IntPtr attr, IntPtr set);

    [LibraryImport("libc")]
    public static partial int posix_spawn_file_actions_init(IntPtr actions);

    [LibraryImport("libc")]
    public static partial int posix_spawn_file_actions_destroy(IntPtr actions);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int posix_spawn_file_actions_addopen(IntPtr actions, int fd, string path, int flags, int mode);

    [LibraryImport("libc")]
    public static partial int posix_spawn_file_actions_adddup2(IntPtr actions, int fd, int newFd);

    [LibraryImport("libc")]
    public static partial int posix_spawn_file_actions_addclosefrom_np(IntPtr actions, int from);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int posix_spawn_file_actions_addchdir_np(IntPtr actions, string path);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int posix_spawnp(out int pid, string file, IntPtr actions, IntPtr attr, IntPtr argv, IntPtr envp);
}
