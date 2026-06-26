using Custodian.App;
using Custodian.App.Services;

namespace Custodian.Tests;

public sealed class TargetUsageRefreshServiceTests
{
    [Fact]
    public void RefreshDriveTargetsForPathUpdatesContainingDriveAndPreservesBadge()
    {
        var oldDrive = new DriveRow(@"C:\ System", @"C:\", "1 GB used", "9 GB free", 10);
        var freshDrive = new DriveRow(@"C:\ System", @"C:\", "2 GB used", "8 GB free", 20);
        var targetRows = new List<TargetRow> { TargetRow.FromDrive(oldDrive, scanCached: true) };
        var driveRows = new List<DriveRow> { oldDrive };

        var refreshed = TargetUsageRefreshService.RefreshDriveTargetsForPath(
            targetRows,
            driveRows,
            [freshDrive],
            @"C:\Users\Me\Temp",
            _ => true,
            _ => false);

        Assert.Equal([@"C:\"], refreshed);
        Assert.Equal("2 GB used", driveRows[0].UsedText);
        Assert.Equal("2 GB used", targetRows[0].UsedText);
        Assert.Equal(20, targetRows[0].UsedPercent);
        Assert.Equal("Scanned", targetRows[0].ScanStatusText);
    }

    [Fact]
    public void RefreshDriveTargetsForPathPreservesActiveBadgeOverCachedBadge()
    {
        var oldDrive = new DriveRow(@"C:\ System", @"C:\", "1 GB used", "9 GB free", 10);
        var freshDrive = new DriveRow(@"C:\ System", @"C:\", "2 GB used", "8 GB free", 20);
        var targetRows = new List<TargetRow> { TargetRow.FromDrive(oldDrive, scanCached: true) };
        var driveRows = new List<DriveRow> { oldDrive };

        TargetUsageRefreshService.RefreshDriveTargetsForPath(
            targetRows,
            driveRows,
            [freshDrive],
            @"C:\Users\Me\Temp",
            _ => true,
            _ => true);

        Assert.Equal("Scanning", targetRows[0].ScanStatusText);
        Assert.Equal("2 GB used", targetRows[0].UsedText);
    }

    [Fact]
    public void RefreshDriveTargetsForPathDoesNotChurnUnrelatedTargets()
    {
        var oldC = new DriveRow(@"C:\ System", @"C:\", "1 GB used", "9 GB free", 10);
        var freshC = new DriveRow(@"C:\ System", @"C:\", "2 GB used", "8 GB free", 20);
        var oldD = new DriveRow(@"D:\ Media", @"D:\", "3 GB used", "7 GB free", 30);
        var freshD = new DriveRow(@"D:\ Media", @"D:\", "4 GB used", "6 GB free", 40);
        var cTarget = TargetRow.FromDrive(oldC);
        var dTarget = TargetRow.FromDrive(oldD);
        var targetRows = new List<TargetRow> { cTarget, dTarget };
        var driveRows = new List<DriveRow> { oldC, oldD };

        var refreshed = TargetUsageRefreshService.RefreshDriveTargetsForPath(
            targetRows,
            driveRows,
            [freshC, freshD],
            @"C:\Users\Me\Temp",
            _ => false,
            _ => false);

        Assert.Equal([@"C:\"], refreshed);
        Assert.NotSame(cTarget, targetRows[0]);
        Assert.Same(dTarget, targetRows[1]);
        Assert.Equal(oldD, driveRows[1]);
    }

    [Fact]
    public void RefreshDriveTargetsForPathsUpdatesSourceAndDestinationDrives()
    {
        var oldC = new DriveRow(@"C:\ System", @"C:\", "1 GB used", "9 GB free", 10);
        var freshC = new DriveRow(@"C:\ System", @"C:\", "2 GB used", "8 GB free", 20);
        var oldD = new DriveRow(@"D:\ Media", @"D:\", "3 GB used", "7 GB free", 30);
        var freshD = new DriveRow(@"D:\ Media", @"D:\", "4 GB used", "6 GB free", 40);
        var targetRows = new List<TargetRow>
        {
            TargetRow.FromDrive(oldC, scanCached: true),
            TargetRow.FromDrive(oldD, scanActive: true)
        };
        var driveRows = new List<DriveRow> { oldC, oldD };

        var refreshed = TargetUsageRefreshService.RefreshDriveTargetsForPaths(
            targetRows,
            driveRows,
            [freshC, freshD],
            [@"C:\Users\Me\Temp", @"D:\Archive"],
            root => string.Equals(root, @"C:\", StringComparison.OrdinalIgnoreCase),
            root => string.Equals(root, @"D:\", StringComparison.OrdinalIgnoreCase));

        Assert.Equal([@"C:\", @"D:\"], refreshed);
        Assert.Equal("2 GB used", targetRows[0].UsedText);
        Assert.Equal("Scanned", targetRows[0].ScanStatusText);
        Assert.Equal("4 GB used", targetRows[1].UsedText);
        Assert.Equal("Scanning", targetRows[1].ScanStatusText);
        Assert.Equal("2 GB used", driveRows[0].UsedText);
        Assert.Equal("4 GB used", driveRows[1].UsedText);
    }

    [Theory]
    [InlineData(@"C:\", @"C:\", true)]
    [InlineData(@"C:\Users\Me", @"C:\", true)]
    [InlineData(@"C:\Users\Me", @"D:\", false)]
    [InlineData(@"C:\Folder2", @"C:\Folder", false)]
    public void IsPathWithinRootUsesPathBoundaries(string path, string rootPath, bool expected)
    {
        Assert.Equal(expected, TargetUsageRefreshService.IsPathWithinRoot(path, rootPath));
    }
}
