using FingerTrap.Sidecar.Abstractions;

namespace FingerTrap.Sidecar.Pty;

internal sealed class UnsupportedPtyService : IPtyService
{
    private const string Message = "PTY backend is only available on Linux and macOS; Windows is deferred (see ADR-0006 and ADR-0007).";

    public event EventHandler<PtyOutputEventArgs>? Output;

    public event EventHandler<PtyExitEventArgs>? Exited;

    public Task<int> SpawnAsync(string sessionId, PtySpawnOptions options, CancellationToken cancellationToken) =>
        throw new PlatformNotSupportedException(Message);

    public ValueTask WriteAsync(string sessionId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        throw new PlatformNotSupportedException(Message);

    public void Resize(string sessionId, int cols, int rows) =>
        throw new PlatformNotSupportedException(Message);

    public void Close(string sessionId) =>
        throw new PlatformNotSupportedException(Message);

    public ValueTask DisposeAsync()
    {
        _ = Output;
        _ = Exited;
        return ValueTask.CompletedTask;
    }
}
