using Custodian.Tui;

namespace Custodian.Tests;

public sealed class TuiLaunchOptionsTests
{
    [Fact]
    public void ParseTreatsCustodianScanArgumentAsOpenFile()
    {
        var options = TuiLaunchOptions.Parse(["C:\\Scans\\data.custodian-scan"]);

        Assert.Equal("C:\\Scans\\data.custodian-scan", options.ScanFilePath);
        Assert.False(options.AutoScan);
    }

    [Fact]
    public void ParseScanCommandCapturesModeAndAllocatedSize()
    {
        var options = TuiLaunchOptions.Parse(["--scan", "D:\\Data", "--mode", "mft", "--allocated"]);

        Assert.Equal("D:\\Data", options.TargetPath);
        Assert.True(options.AutoScan);
        Assert.Equal("mft", options.Mode);
        Assert.True(options.CollectAllocatedSize);
    }

    [Fact]
    public void ParseUsesElevatedLaunchPathWhenProvided()
    {
        var options = TuiLaunchOptions.Parse([], "E:\\Root");

        Assert.Equal("E:\\Root", options.TargetPath);
    }
}
