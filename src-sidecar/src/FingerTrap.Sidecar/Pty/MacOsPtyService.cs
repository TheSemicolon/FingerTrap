using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using FingerTrap.Sidecar.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace FingerTrap.Sidecar.Pty;

internal sealed class MacOsPtyService : IPtyService
{
    private const int ResizeDebounceMilliseconds = 50;

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public event EventHandler<PtyOutputEventArgs>? Output;

    public event EventHandler<PtyExitEventArgs>? Exited;

    public Task<int> SpawnAsync(string sessionId, PtySpawnOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var session = StartSession(sessionId, options);
        if (!_sessions.TryAdd(sessionId, session))
        {
            session.Kill();
            throw new InvalidOperationException($"session '{sessionId}' is already active");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _sessions.TryRemove(sessionId, out _);
            session.Kill();
            cancellationToken.ThrowIfCancellationRequested();
        }

        StartReadLoop(session);
        StartExitWatcher(session);
        return Task.FromResult(session.Pid);
    }

    public ValueTask WriteAsync(string sessionId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new InvalidOperationException($"session '{sessionId}' is not active");
        }

        var span = data.Span;
        var offset = 0;
        while (offset < span.Length)
        {
            int written;
            unsafe
            {
                fixed (byte* p = span)
                {
                    written = (int)MacOsNativeMethods.Write(session.MasterFd, p + offset, (nuint)(span.Length - offset));
                }
            }

            if (written < 0)
            {
                var err = Marshal.GetLastPInvokeError();
                if (err == 4) // EINTR — same value on Darwin and Linux (POSIX)
                {
                    continue;
                }

                throw new InvalidOperationException($"write(master) failed: errno={err}");
            }

            if (written == 0)
            {
                break;
            }

            offset += written;
        }

        return ValueTask.CompletedTask;
    }

    public void Resize(string sessionId, int cols, int rows)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        session.QueueResize(cols, rows);
    }

    public void Close(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.Kill();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            session.Kill();
        }

        _sessions.Clear();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static Session StartSession(string sessionId, PtySpawnOptions options)
    {
        var masterFd = MacOsNativeMethods.PosixOpenpt(MacOsNativeMethods.O_RDWR | MacOsNativeMethods.O_NOCTTY);
        if (masterFd < 0)
        {
            ThrowErrno("posix_openpt");
        }

        if (MacOsNativeMethods.Fcntl(masterFd, MacOsNativeMethods.F_SETFD, MacOsNativeMethods.FD_CLOEXEC) < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            MacOsNativeMethods.Close(masterFd);
            throw new InvalidOperationException($"fcntl(FD_CLOEXEC) failed: errno={err}");
        }

        if (MacOsNativeMethods.Grantpt(masterFd) < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            MacOsNativeMethods.Close(masterFd);
            throw new InvalidOperationException($"grantpt failed: errno={err}");
        }

        if (MacOsNativeMethods.Unlockpt(masterFd) < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            MacOsNativeMethods.Close(masterFd);
            throw new InvalidOperationException($"unlockpt failed: errno={err}");
        }

        // macOS uses TIOCPTYGNAME instead of ptsname_r — avoids the 10.13.4
        // availability cliff and is a stable Darwin BSD ioctl.
        var slavePathBuf = new byte[MacOsNativeMethods.TIOCPTYGNAME_BUFLEN];
        if (MacOsNativeMethods.IoctlBuffer(masterFd, MacOsNativeMethods.TIOCPTYGNAME, slavePathBuf) < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            MacOsNativeMethods.Close(masterFd);
            throw new InvalidOperationException($"ioctl(TIOCPTYGNAME) failed: errno={err}");
        }

        var nullTerminator = Array.IndexOf(slavePathBuf, (byte)0);
        var slavePath = Encoding.UTF8.GetString(slavePathBuf, 0, nullTerminator < 0 ? slavePathBuf.Length : nullTerminator);

        var initialSize = new MacOsNativeMethods.WinSize
        {
            ws_row = (ushort)Math.Max(1, options.Rows),
            ws_col = (ushort)Math.Max(1, options.Cols),
        };
        if (MacOsNativeMethods.IoctlWinSize(masterFd, MacOsNativeMethods.TIOCSWINSZ, ref initialSize) < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            MacOsNativeMethods.Close(masterFd);
            throw new InvalidOperationException($"ioctl(TIOCSWINSZ) failed: errno={err}");
        }

        var shellPath = ResolveShell(options.Shell);
        var argv = BuildArgv(shellPath);
        var envp = BuildEnvp(options.Env, options.Cols, options.Rows);

        // posix_spawnattr_t and posix_spawn_file_actions_t on macOS are
        // opaque pointer typedefs; init() heap-allocates and writes the
        // pointer into the supplied slot. We hold them as nint and pass by
        // value to the rest of the API, by ref to destroy.
        nint attr = 0;
        nint actions = 0;
        var attrInitialised = false;
        var actionsInitialised = false;
        int pid;
        try
        {
            if (MacOsNativeMethods.PosixSpawnattrInit(out attr) != 0)
            {
                ThrowErrno("posix_spawnattr_init");
            }

            attrInitialised = true;

            if (MacOsNativeMethods.PosixSpawnattrSetflags(attr, MacOsNativeMethods.POSIX_SPAWN_SETSID) != 0)
            {
                ThrowErrno("posix_spawnattr_setflags");
            }

            if (MacOsNativeMethods.PosixSpawnFileActionsInit(out actions) != 0)
            {
                ThrowErrno("posix_spawn_file_actions_init");
            }

            actionsInitialised = true;

            if (MacOsNativeMethods.PosixSpawnFileActionsAddopen(actions, 0, slavePath, MacOsNativeMethods.O_RDWR, 0) != 0)
            {
                ThrowErrno("posix_spawn_file_actions_addopen");
            }

            if (MacOsNativeMethods.PosixSpawnFileActionsAdddup2(actions, 0, 1) != 0)
            {
                ThrowErrno("posix_spawn_file_actions_adddup2(1)");
            }

            if (MacOsNativeMethods.PosixSpawnFileActionsAdddup2(actions, 0, 2) != 0)
            {
                ThrowErrno("posix_spawn_file_actions_adddup2(2)");
            }

            if (!string.IsNullOrEmpty(options.Cwd))
            {
                if (MacOsNativeMethods.PosixSpawnFileActionsAddchdirNp(actions, options.Cwd) != 0)
                {
                    ThrowErrno("posix_spawn_file_actions_addchdir_np");
                }
            }

            int spawnResult;
            unsafe
            {
                fixed (byte** argvPtr = argv.Pointers)
                fixed (byte** envpPtr = envp.Pointers)
                {
                    spawnResult = MacOsNativeMethods.PosixSpawnp(out pid, shellPath, actions, attr, argvPtr, envpPtr);
                }
            }

            if (spawnResult != 0)
            {
                throw new InvalidOperationException($"posix_spawnp failed: errno={spawnResult}");
            }
        }
        catch
        {
            MacOsNativeMethods.Close(masterFd);
            throw;
        }
        finally
        {
            if (actionsInitialised)
            {
                MacOsNativeMethods.PosixSpawnFileActionsDestroy(ref actions);
            }

            if (attrInitialised)
            {
                MacOsNativeMethods.PosixSpawnattrDestroy(ref attr);
            }

            argv.Dispose();
            envp.Dispose();
        }

        // FileStream wraps the master fd. isAsync:false is intentional — PTY
        // fds are not O_ASYNC; ReadAsync still works (BCL trampolines via
        // Task.Run for the syscall). Mirrors the Linux service.
        var safeHandle = new SafeFileHandle(masterFd, ownsHandle: true);
        var stream = new FileStream(safeHandle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        return new Session(sessionId, pid, masterFd, stream);
    }

    private void StartReadLoop(Session session)
    {
        _ = Task.Run(async () =>
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                while (true)
                {
                    int read;
                    try
                    {
                        read = await session.MasterStream.ReadAsync(buffer.AsMemory(), session.Cancellation.Token).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // Linux signals master EOF as EIO (IOException). macOS
                        // signals it as a clean 0-byte read instead, so this
                        // catch is mostly defensive on Darwin but harmless.
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    var copy = new byte[read];
                    Buffer.BlockCopy(buffer, 0, copy, 0, read);
                    Output?.Invoke(this, new PtyOutputEventArgs
                    {
                        SessionId = session.Id,
                        Data = copy,
                    });
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        });
    }

    private void StartExitWatcher(Session session)
    {
        _ = Task.Run(() =>
        {
            while (true)
            {
                var result = MacOsNativeMethods.Waitpid(session.Pid, out var status, 0);
                if (result == session.Pid)
                {
                    int exitCode;
                    if (MacOsNativeMethods.WIfExited(status))
                    {
                        exitCode = MacOsNativeMethods.WExitStatus(status);
                    }
                    else
                    {
                        exitCode = 128 + MacOsNativeMethods.WTermSig(status);
                    }

                    _sessions.TryRemove(session.Id, out _);
                    session.Cleanup();
                    Exited?.Invoke(this, new PtyExitEventArgs
                    {
                        SessionId = session.Id,
                        ExitCode = exitCode,
                    });
                    return;
                }

                if (result < 0 && Marshal.GetLastPInvokeError() == 4) // EINTR
                {
                    continue;
                }

                return;
            }
        });
    }

    private static string ResolveShell(string? requested)
    {
        if (!string.IsNullOrEmpty(requested))
        {
            return requested;
        }

        var fromEnv = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }

        // macOS Catalina+ default. /bin/bash on macOS is bash 3.2 (GPL2,
        // unmaintained); /bin/zsh is the modern user default. /bin/sh is the
        // last-resort fallback present on every macOS install.
        return File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/sh";
    }

    private static NativeStringArray BuildArgv(string shellPath)
    {
        return NativeStringArray.FromValues(new[] { shellPath });
    }

    private static NativeStringArray BuildEnvp(IReadOnlyDictionary<string, string>? overrides, int cols, int rows)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string k && entry.Value is string v)
            {
                merged[k] = v;
            }
        }

        merged["TERM"] = merged.TryGetValue("TERM", out var existingTerm) && !string.IsNullOrEmpty(existingTerm)
            ? existingTerm
            : "xterm-256color";
        merged["COLUMNS"] = cols.ToString(System.Globalization.CultureInfo.InvariantCulture);
        merged["LINES"] = rows.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (overrides is not null)
        {
            foreach (var (k, v) in overrides)
            {
                merged[k] = v;
            }
        }

        var formatted = new string[merged.Count];
        var i = 0;
        foreach (var (k, v) in merged)
        {
            formatted[i++] = $"{k}={v}";
        }

        return NativeStringArray.FromValues(formatted);
    }

    private static void ThrowErrno(string call)
    {
        var err = Marshal.GetLastPInvokeError();
        throw new InvalidOperationException($"{call} failed: errno={err}");
    }

    private sealed class Session
    {
        public Session(string id, int pid, int masterFd, FileStream stream)
        {
            Id = id;
            Pid = pid;
            MasterFd = masterFd;
            MasterStream = stream;
        }

        public string Id { get; }

        public int Pid { get; }

        public int MasterFd { get; }

        public FileStream MasterStream { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        private readonly object _resizeLock = new();
        private int _pendingCols;
        private int _pendingRows;
        private Timer? _resizeTimer;

        public void QueueResize(int cols, int rows)
        {
            lock (_resizeLock)
            {
                _pendingCols = cols;
                _pendingRows = rows;
                _resizeTimer ??= new Timer(_ => ApplyPendingResize(), null, Timeout.Infinite, Timeout.Infinite);
                _resizeTimer.Change(ResizeDebounceMilliseconds, Timeout.Infinite);
            }
        }

        private void ApplyPendingResize()
        {
            // Hold _resizeLock across cancellation check and ioctl so Cleanup
            // cannot dispose the master fd between the two and leave ioctl
            // issuing TIOCSWINSZ on a closed (or reused) descriptor.
            lock (_resizeLock)
            {
                if (Cancellation.IsCancellationRequested)
                {
                    return;
                }

                var ws = new MacOsNativeMethods.WinSize
                {
                    ws_col = (ushort)Math.Max(1, _pendingCols),
                    ws_row = (ushort)Math.Max(1, _pendingRows),
                };
                MacOsNativeMethods.IoctlWinSize(MasterFd, MacOsNativeMethods.TIOCSWINSZ, ref ws);
            }
        }

        public void Kill()
        {
            try
            {
                MacOsNativeMethods.Kill(Pid, 15); // SIGTERM (same value on Darwin)
            }
            catch
            {
                // best-effort
            }

            Cleanup();
        }

        public void Cleanup()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            Timer? timer;
            lock (_resizeLock)
            {
                // Dispose under _resizeLock so an in-flight ApplyPendingResize
                // either sees cancellation and bails, or finishes its ioctl
                // before the master fd closes.
                try
                {
                    MasterStream.Dispose();
                }
                catch
                {
                    // best-effort
                }

                timer = _resizeTimer;
                _resizeTimer = null;
            }

            timer?.Dispose();

            try
            {
                Cancellation.Dispose();
            }
            catch
            {
                // best-effort
            }
        }
    }

    private sealed unsafe class NativeStringArray : IDisposable
    {
        private readonly nint[] _allocations;
        public byte*[] Pointers { get; }

        private NativeStringArray(nint[] allocations, byte*[] pointers)
        {
            _allocations = allocations;
            Pointers = pointers;
        }

        public static NativeStringArray FromValues(string[] values)
        {
            var allocations = new nint[values.Length];
            var pointers = new byte*[values.Length + 1];
            for (var i = 0; i < values.Length; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(values[i] + '\0');
                var ptr = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                allocations[i] = ptr;
                pointers[i] = (byte*)ptr;
            }

            pointers[values.Length] = null;
            return new NativeStringArray(allocations, pointers);
        }

        public void Dispose()
        {
            foreach (var ptr in _allocations)
            {
                if (ptr != default)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }
    }
}
