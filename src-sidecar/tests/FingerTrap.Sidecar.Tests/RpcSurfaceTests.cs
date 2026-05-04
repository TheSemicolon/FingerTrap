using FingerTrap.Sidecar.Ipc;
using Xunit;

namespace FingerTrap.Sidecar.Tests;

public sealed class RpcSurfaceTests
{
    [Fact]
    public async Task PingAsync_ReturnsPongPrefixedEcho()
    {
        var surface = new RpcSurface();

        var result = await surface.PingAsync("hello");

        Assert.Equal("pong: hello", result);
    }

    [Fact]
    public async Task PingAsync_HandlesEmptyMessage()
    {
        var surface = new RpcSurface();

        var result = await surface.PingAsync(string.Empty);

        Assert.Equal("pong: ", result);
    }
}
