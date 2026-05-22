using Custodian.Core.Updates;

namespace Custodian.Tests;

public sealed class AppUpdateStatusTests
{
    [Fact]
    public void Available_IncludesCurrentAndAvailableVersions()
    {
        var status = AppUpdateStatusFactory.Available("1.2.0", "1.1.0");

        Assert.Equal(AppUpdateStatusKind.Available, status.Kind);
        Assert.Equal("1.1.0", status.CurrentVersion);
        Assert.Equal("1.2.0", status.AvailableVersion);
        Assert.Contains("1.2.0", status.Message);
        Assert.Contains("1.1.0", status.Message);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(37, 37)]
    [InlineData(101, 100)]
    public void Downloading_ClampsProgress(int input, int expected)
    {
        var status = AppUpdateStatusFactory.Downloading("1.2.0", input);

        Assert.Equal(AppUpdateStatusKind.Downloading, status.Kind);
        Assert.Equal(expected, status.ProgressPercent);
        Assert.Contains($"{expected}%", status.Message);
    }

    [Fact]
    public void NotInstalled_ExplainsInstalledAppRequirement()
    {
        var status = AppUpdateStatusFactory.NotInstalled();

        Assert.Equal(AppUpdateStatusKind.NotInstalled, status.Kind);
        Assert.Contains("installed", status.Message);
    }
}
