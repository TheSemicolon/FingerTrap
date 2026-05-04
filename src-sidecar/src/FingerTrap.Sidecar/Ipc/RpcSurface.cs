using FingerTrap.Sidecar.Abstractions;
using StreamJsonRpc;

namespace FingerTrap.Sidecar.Ipc;

internal sealed class RpcSurface : IDisposable
{
    private readonly IPtyService _pty;
    private JsonRpc? _rpc;
    private bool _eventsBound;

    public RpcSurface(IPtyService pty)
    {
        _pty = pty;
    }

    public void AttachRpc(JsonRpc rpc)
    {
        _rpc = rpc;
        if (_eventsBound)
        {
            return;
        }

        _pty.Output += OnPtyOutput;
        _pty.Exited += OnPtyExit;
        _eventsBound = true;
    }

    public void Dispose()
    {
        if (_eventsBound)
        {
            _pty.Output -= OnPtyOutput;
            _pty.Exited -= OnPtyExit;
            _eventsBound = false;
        }
    }

#pragma warning disable CA1822 // RPC targets must be instance methods (StreamJsonRpc.AddLocalRpcTarget)
    public Task<string> PingAsync(string message) =>
        Task.FromResult($"pong: {message}");
#pragma warning restore CA1822

    [JsonRpcMethod("pty/spawn")]
    public async Task<PtySpawnResult> PtySpawnAsync(PtySpawnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = new PtySpawnOptions(request.Shell, request.Cwd, request.Cols, request.Rows, request.Env);
        var pid = await _pty.SpawnAsync(request.SessionId, options, cancellationToken).ConfigureAwait(false);
        return new PtySpawnResult(pid);
    }

    [JsonRpcMethod("pty/write")]
    public async Task PtyWriteAsync(PtyWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bytes = Convert.FromBase64String(request.DataBase64);
        await _pty.WriteAsync(request.SessionId, bytes, cancellationToken).ConfigureAwait(false);
    }

    [JsonRpcMethod("pty/resize")]
    public Task PtyResizeAsync(PtyResizeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _pty.Resize(request.SessionId, request.Cols, request.Rows);
        return Task.CompletedTask;
    }

    private void OnPtyOutput(object? sender, PtyOutputEventArgs e)
    {
        var rpc = _rpc;
        if (rpc is null)
        {
            return;
        }

        var payload = new PtyOutputNotification(e.SessionId, Convert.ToBase64String(e.Data.Span));
        _ = rpc.NotifyAsync("pty/output", payload);
    }

    private void OnPtyExit(object? sender, PtyExitEventArgs e)
    {
        var rpc = _rpc;
        if (rpc is null)
        {
            return;
        }

        var payload = new PtyExitNotification(e.SessionId, e.ExitCode);
        _ = rpc.NotifyAsync("pty/exit", payload);
    }
}
