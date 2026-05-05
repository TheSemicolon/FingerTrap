using System.Collections.Generic;

namespace FingerTrap.Sidecar.Abstractions;

public interface IPtyService : IAsyncDisposable
{
    public Task<int> SpawnAsync(string sessionId, PtySpawnOptions options, CancellationToken cancellationToken);

    public ValueTask WriteAsync(string sessionId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    public void Resize(string sessionId, int cols, int rows);

    public void Close(string sessionId);

    public event EventHandler<PtyOutputEventArgs>? Output;

    public event EventHandler<PtyExitEventArgs>? Exited;
}

public sealed record PtySpawnOptions(
    string? Shell,
    string? Cwd,
    int Cols,
    int Rows,
    IReadOnlyDictionary<string, string>? Env);

public sealed class PtyOutputEventArgs : EventArgs
{
    public required string SessionId { get; init; }

    public required ReadOnlyMemory<byte> Data { get; init; }
}

public sealed class PtyExitEventArgs : EventArgs
{
    public required string SessionId { get; init; }

    public required int ExitCode { get; init; }
}
