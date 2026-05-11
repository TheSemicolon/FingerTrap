using System.Buffers;
using System.Collections.Concurrent;
using FingerTrap.Sidecar.Abstractions;

namespace FingerTrap.Sidecar.Pty;

/// <summary>
/// Adapter that maps <see cref="IPtyService"/> onto Porta.Pty's
/// <see cref="global::Porta.Pty.PtyProvider"/>. Porta.Pty handles
/// platform branching (Mac/Linux/Windows) internally via a non-variadic
/// C shim — see ADR-0008.
/// </summary>
internal sealed class PtyService : IPtyService
{
    private const int ResizeDebounceMs = 50;

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public event EventHandler<PtyOutputEventArgs>? Output;

    public event EventHandler<PtyExitEventArgs>? Exited;

    public async Task<int> SpawnAsync(string sessionId, PtySpawnOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var shellPath = ResolveShell(options.Shell);

        var ptyOptions = new global::Porta.Pty.PtyOptions
        {
            App = shellPath,
            CommandLine = Array.Empty<string>(),
            Cwd = string.IsNullOrEmpty(options.Cwd) ? Environment.CurrentDirectory : options.Cwd,
            Cols = Math.Max(1, options.Cols),
            Rows = Math.Max(1, options.Rows),
            Environment = options.Env is not null
                ? new Dictionary<string, string>(options.Env)
                : new Dictionary<string, string>(),
        };

        var connection = await global::Porta.Pty.PtyProvider.SpawnAsync(ptyOptions, cancellationToken).ConfigureAwait(false);

        var session = new Session(sessionId, connection);
        if (!_sessions.TryAdd(sessionId, session))
        {
            session.Dispose();
            throw new InvalidOperationException($"session '{sessionId}' is already active");
        }

        connection.ProcessExited += (_, e) =>
        {
            if (_sessions.TryRemove(sessionId, out var removed))
            {
                removed.Dispose();
            }

            Exited?.Invoke(this, new PtyExitEventArgs
            {
                SessionId = sessionId,
                ExitCode = e.ExitCode,
            });
        };

        StartReadLoop(session);
        return connection.Pid;
    }

    public async ValueTask WriteAsync(string sessionId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new InvalidOperationException($"session '{sessionId}' is not active");
        }

        await session.Connection.WriterStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await session.Connection.WriterStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Resize(string sessionId, int cols, int rows)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.QueueResize(cols, rows);
        }
    }

    public void Close(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
        return ValueTask.CompletedTask;
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

        // macOS Catalina+ default is zsh; /bin/bash on macOS is bash 3.2
        // (frozen at GPL2). Linux defaults to bash. /bin/sh is the
        // last-resort fallback present on every POSIX system.
        if (File.Exists("/bin/zsh"))
        {
            return "/bin/zsh";
        }

        if (File.Exists("/bin/bash"))
        {
            return "/bin/bash";
        }

        return "/bin/sh";
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
                        read = await session.Connection.ReaderStream
                            .ReadAsync(buffer.AsMemory(), session.Cancellation.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (IOException)
                    {
                        // Linux signals master EOF as EIO (IOException).
                        // macOS signals it as a clean 0-byte read.
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

    private sealed class Session : IDisposable
    {
        private readonly object _resizeLock = new();
        private int _pendingCols;
        private int _pendingRows;
        private Timer? _resizeTimer;
        private bool _disposed;

        public Session(string id, global::Porta.Pty.IPtyConnection connection)
        {
            Id = id;
            Connection = connection;
        }

        public string Id { get; }

        public global::Porta.Pty.IPtyConnection Connection { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public void QueueResize(int cols, int rows)
        {
            lock (_resizeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _pendingCols = cols;
                _pendingRows = rows;
                _resizeTimer ??= new Timer(_ => ApplyPendingResize(), null, Timeout.Infinite, Timeout.Infinite);
                _resizeTimer.Change(ResizeDebounceMs, Timeout.Infinite);
            }
        }

        private void ApplyPendingResize()
        {
            int cols;
            int rows;
            lock (_resizeLock)
            {
                if (_disposed)
                {
                    return;
                }

                cols = _pendingCols;
                rows = _pendingRows;
            }

            try
            {
                Connection.Resize(Math.Max(1, cols), Math.Max(1, rows));
            }
            catch
            {
                // best-effort; connection may have been killed concurrently
            }
        }

        public void Dispose()
        {
            lock (_resizeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            try
            {
                Cancellation.Cancel();
            }
            catch
            {
                // best-effort
            }

            _resizeTimer?.Dispose();
            _resizeTimer = null;

            try
            {
                Connection.Dispose();
            }
            catch
            {
                // best-effort
            }

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
}
