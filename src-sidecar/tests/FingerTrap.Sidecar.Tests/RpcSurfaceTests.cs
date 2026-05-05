using System.Text;
using FingerTrap.Sidecar.Abstractions;
using FingerTrap.Sidecar.Ipc;
using NSubstitute;
using Xunit;

namespace FingerTrap.Sidecar.Tests;

public sealed class RpcSurfaceTests
{
    [Fact]
    public async Task PingAsync_ReturnsPongPrefixedEcho()
    {
        using var surface = new RpcSurface(Substitute.For<IPtyService>());

        var result = await surface.PingAsync("hello");

        Assert.Equal("pong: hello", result);
    }

    [Fact]
    public async Task PingAsync_HandlesEmptyMessage()
    {
        using var surface = new RpcSurface(Substitute.For<IPtyService>());

        var result = await surface.PingAsync(string.Empty);

        Assert.Equal("pong: ", result);
    }

    [Fact]
    public async Task PtySpawnAsync_ForwardsOptionsAndWrapsPid()
    {
        var pty = Substitute.For<IPtyService>();
        pty.SpawnAsync("s1", Arg.Any<PtySpawnOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(4242));
        using var surface = new RpcSurface(pty);

        var request = new PtySpawnRequest("s1", "/usr/bin/zsh", "/tmp", 100, 30, null);
        var result = await surface.PtySpawnAsync(request, CancellationToken.None);

        Assert.Equal(4242, result.Pid);
        await pty.Received(1).SpawnAsync(
            "s1",
            Arg.Is<PtySpawnOptions>(o => o.Shell == "/usr/bin/zsh" && o.Cwd == "/tmp" && o.Cols == 100 && o.Rows == 30),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PtyWriteAsync_DecodesBase64AndForwardsBytes()
    {
        var pty = Substitute.For<IPtyService>();
        using var surface = new RpcSurface(pty);
        var bytes = Encoding.UTF8.GetBytes("ls -la\n");
        var request = new PtyWriteRequest("s1", Convert.ToBase64String(bytes));

        await surface.PtyWriteAsync(request, CancellationToken.None);

        await pty.Received(1).WriteAsync(
            "s1",
            Arg.Is<ReadOnlyMemory<byte>>(m => MemoryEquals(m, bytes)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PtyResizeAsync_ForwardsDimensions()
    {
        var pty = Substitute.For<IPtyService>();
        using var surface = new RpcSurface(pty);

        await surface.PtyResizeAsync(new PtyResizeRequest("s1", 120, 40));

        pty.Received(1).Resize("s1", 120, 40);
    }

    private static bool MemoryEquals(ReadOnlyMemory<byte> memory, byte[] expected) =>
        memory.Span.SequenceEqual(expected);
}
