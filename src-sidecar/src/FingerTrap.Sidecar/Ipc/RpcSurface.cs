namespace FingerTrap.Sidecar.Ipc;

internal sealed class RpcSurface
{
#pragma warning disable CA1822 // RPC targets must be instance methods (StreamJsonRpc.AddLocalRpcTarget)
    public Task<string> PingAsync(string message) =>
        Task.FromResult($"pong: {message}");
#pragma warning restore CA1822
}
