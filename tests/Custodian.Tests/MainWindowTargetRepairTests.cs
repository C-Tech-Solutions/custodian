using Custodian.App;
using Custodian.App.Services;
using Custodian.Core.Model;

public sealed class MainWindowTargetRepairTests
{
    [Fact]
    public void FindDriveByVolumeLabelMatchesPathLikeDriveLabelBeforePathRejection()
    {
        var drive = new DriveRow(@"D:\ Library", @"D:\", "1 GB used", "2 GB free", 50);

        var match = MainWindow.FindDriveByVolumeLabel([drive], @"D:\ Library");

        Assert.Same(drive, match);
    }

    [Fact]
    public void FindDriveByVolumeLabelMatchesBareVolumeLabel()
    {
        var drive = new DriveRow(@"D:\ Library", @"D:\", "1 GB used", "2 GB free", 50);

        var match = MainWindow.FindDriveByVolumeLabel([drive], "Library");

        Assert.Same(drive, match);
    }

    [Fact]
    public void PortableTargetMatchesScanUsesStableTargetIdWhenStorageObjectIdChanges()
    {
        var target = new PortableDeviceTarget(
            "wpd:stable-storage",
            "phone-device-id",
            "Pixel",
            "current-storage-object-id",
            "Internal storage",
            "Pixel/Internal storage",
            null,
            null,
            IsAvailable: true,
            "Portable device storage");
        var scan = new ScanResult
        {
            RootPath = "wpd:stable-storage",
            SourceId = "wpd:stable-storage",
            SourceKind = ScanSourceKind.PortableDevice,
            PortableDeviceId = "phone-device-id",
            PortableStorageObjectId = "stale-storage-object-id"
        };

        Assert.True(MainWindow.PortableTargetMatchesScan(target, scan));
    }

    [Fact]
    public void PortableTargetMatchesScanFallsBackToStorageNameForLegacySavedScan()
    {
        var target = new PortableDeviceTarget(
            "wpd:stable-storage",
            "phone-device-id",
            "Pixel",
            "current-storage-object-id",
            "Internal storage",
            "Pixel/Internal storage",
            null,
            null,
            IsAvailable: true,
            "Portable device storage");
        var scan = new ScanResult
        {
            RootPath = "wpd:old-object-id-target",
            SourceId = "wpd:old-object-id-target",
            SourceKind = ScanSourceKind.PortableDevice,
            PortableDeviceId = "phone-device-id",
            PortableStorageObjectId = "stale-storage-object-id",
            PortableStorageName = "Internal storage"
        };

        Assert.True(MainWindow.PortableTargetMatchesScan(target, scan));
    }

    [Fact]
    public void FindPortableTargetRowForScanPrefersExactIdentityBeforeNameFallback()
    {
        var nameFallbackTarget = new PortableDeviceTarget(
            "wpd:first",
            "phone-device-id",
            "Pixel",
            "first-storage-object-id",
            "Internal storage",
            "Pixel/Internal storage",
            null,
            null,
            IsAvailable: true,
            "Portable device storage");
        var exactTarget = nameFallbackTarget with
        {
            TargetId = "wpd:second",
            StorageObjectId = "second-storage-object-id"
        };
        var scan = new ScanResult
        {
            RootPath = "wpd:second",
            SourceId = "wpd:second",
            SourceKind = ScanSourceKind.PortableDevice,
            PortableDeviceId = "phone-device-id",
            PortableStorageObjectId = "second-storage-object-id",
            PortableStorageName = "Internal storage"
        };

        var match = MainWindow.FindPortableTargetRowForScan(
            [TargetRow.FromPortable(nameFallbackTarget), TargetRow.FromPortable(exactTarget)],
            scan);

        Assert.NotNull(match);
        Assert.Same(exactTarget, match.PortableTarget);
    }
}
