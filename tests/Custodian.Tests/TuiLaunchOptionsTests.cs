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
    public void ResolveRelativePathsMakesScanAndOpenPathsAbsolute()
    {
        var resolved = TuiLaunchOptions.ResolveRelativePaths(
            ["--open", "data.custodian-scan", "--scan", @"..\Data", "--mode", "mft", "--allocated"],
            @"C:\Users\Owner\Scans");

        Assert.Equal(
            ["--open", @"C:\Users\Owner\Scans\data.custodian-scan", "--scan", @"C:\Users\Owner\Data", "--mode", "mft", "--allocated"],
            resolved);
    }

    [Fact]
    public void ResolveRelativePathsMakesPositionalPathsAbsolute()
    {
        var resolved = TuiLaunchOptions.ResolveRelativePaths(["data.custodian-scan"], @"C:\Users\Owner\Scans");
        var options = TuiLaunchOptions.Parse(resolved);

        Assert.Equal(@"C:\Users\Owner\Scans\data.custodian-scan", options.ScanFilePath);
    }

    [Theory]
    [InlineData(@"D:\Data")]
    [InlineData("C:")]
    [InlineData(@"\\server\share\folder")]
    public void ResolveRelativePathsLeavesRootedPathsAsTyped(string path)
    {
        var resolved = TuiLaunchOptions.ResolveRelativePaths(["--scan", path, path], @"C:\Users\Owner");

        Assert.Equal(["--scan", path, path], resolved);
    }

    [Fact]
    public void ResolveRelativePathsLeavesOptionValuesAndHookArgumentsAlone()
    {
        var resolved = TuiLaunchOptions.ResolveRelativePaths(
            ["--custodian-path", "E:", "--mode", "recursive", "--veloapp-version"],
            @"C:\Users\Owner");

        Assert.Equal(["--custodian-path", "E:", "--mode", "recursive", "--veloapp-version"], resolved);
    }

    [Fact]
    public void ParseUsesElevatedLaunchPathWhenProvided()
    {
        var options = TuiLaunchOptions.Parse([], "E:\\Root");

        Assert.Equal("E:\\Root", options.TargetPath);
    }
}
