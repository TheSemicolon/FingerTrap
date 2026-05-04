using FingerTrap.Sidecar.Abstractions;

namespace FingerTrap.Sidecar.Pty;

internal sealed class UnsupportedPtyService : IPtyService
{
    public event EventHandler<PtyOutputEventArgs>? Output;

    public event EventHandler<PtyExitEventArgs>? Exited;

    public Task<int> SpawnAsync(string sessionId, PtySpawnOptions options, CancellationToken cancellationToken) =>
        throw new PlatformNotSupportedException("PTY backend is only available on Linux at M1 (see ADR-0006).");

    public ValueTask WriteAsync(string sessionId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        throw new PlatformNotSupportedException();

    public void Resize(string sessionId, int cols, int rows) =>
        throw new PlatformNotSupportedException();

    public void Close(string sessionId) =>
        throw new PlatformNotSupportedException();

    public ValueTask DisposeAsync()
    {
        _ = Output;
        _ = Exited;
        return ValueTask.CompletedTask;
    }
}
