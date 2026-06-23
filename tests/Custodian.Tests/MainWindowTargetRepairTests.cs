using Custodian.App;
using Custodian.App.Services;
using Custodian.Core.Model;
using Custodian.Core.Presentation;

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
    public void FindLocalVolumeProjectionRepairDriveUsesPreviousPortableLabel()
    {
        var drive = new DriveRow(@"D:\ Library", @"D:\", "1 GB used", "2 GB free", 50);
        var portableProjection = TargetRow.FromPortable(PortableDeviceTarget.Unavailable(
            @"SWD\WPDBUSENUM\{657BF548-8DCB-11EE-99AA-806E6F6E6963}#0000000000100000",
            "Library",
            "Unlock the phone and choose USB File Transfer mode."));

        var match = MainWindow.FindLocalVolumeProjectionRepairDrive([drive], string.Empty, portableProjection);

        Assert.Same(drive, match);
    }

    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"D:\ Library")]
    [InlineData("Library")]
    public void FindFilesystemTargetForScanTextResolvesDriveBeforePortableProjection(string text)
    {
        var drive = new DriveRow(@"D:\ Library", @"D:\", "1 GB used", "2 GB free", 50);
        var driveTarget = TargetRow.FromDrive(drive);
        var portableProjection = TargetRow.FromPortable(PortableDeviceTarget.Unavailable(
            @"SWD\WPDBUSENUM\{657BF548-8DCB-11EE-99AA-806E6F6E6963}#0000000000100000",
            "Library",
            "Unlock the phone and choose USB File Transfer mode."));

        var match = MainWindow.FindFilesystemTargetForScanText(
            [portableProjection, driveTarget],
            [drive],
            text);

        Assert.NotNull(match);
        Assert.Equal(TargetKind.Drive, match.Kind);
        Assert.Equal(@"D:\", match.RootPath);
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

    [Fact]
    public void PortableCopyEntriesFromSelectedRowsRequiresExplicitRows()
    {
        var entries = MainWindow.PortableCopyEntriesFromSelectedRows([]);

        Assert.Empty(entries);
    }

    [Fact]
    public void PortableCopyEntriesFromSelectedRowsReturnsSelectedRowEntries()
    {
        var entry = new FileSystemEntry
        {
            Name = "photo.jpg",
            FullPath = "Pixel/Internal storage/DCIM/photo.jpg",
            LogicalSizeBytes = 1024,
            AllocatedSizeBytes = 1024
        };
        var row = DetailRow.From(entry, parentBytes: 1024);

        var entries = MainWindow.PortableCopyEntriesFromSelectedRows([row]);

        var selectedEntry = Assert.Single(entries);
        Assert.Same(entry, selectedEntry);
    }
}
