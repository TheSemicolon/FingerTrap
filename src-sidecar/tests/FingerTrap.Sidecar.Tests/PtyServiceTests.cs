using FingerTrap.Sidecar.Pty;
using Xunit;

namespace FingerTrap.Sidecar.Tests;

public sealed class PtyServiceTests
{
    [Fact]
    public void ResolveCwd_NullRequest_ReturnsUserProfile()
    {
        var result = PtyService.ResolveCwd(null);

        var expected = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolveCwd_EmptyRequest_ReturnsUserProfile()
    {
        var result = PtyService.ResolveCwd(string.Empty);

        var expected = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolveCwd_ExplicitPath_ReturnsThatPath()
    {
        var result = PtyService.ResolveCwd("/tmp/foo");

        Assert.Equal("/tmp/foo", result);
    }

    [Fact]
    public void ResolveCwd_WhitespaceRequest_ReturnsRequestAsIs()
    {
        // We intentionally do not trim. An explicit-but-weird cwd is
        // the caller's choice; the spawn will fail validation downstream
        // if the directory doesn't exist, which is the right error path.
        var result = PtyService.ResolveCwd("   ");

        Assert.Equal("   ", result);
    }
}
