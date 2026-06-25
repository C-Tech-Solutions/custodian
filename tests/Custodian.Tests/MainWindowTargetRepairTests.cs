using Custodian.App;
using Custodian.App.Services;
using Custodian.Core.Model;
using Custodian.Core.Presentation;
using Custodian.Platform.Windows.Services;

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

    [Theory]
    [InlineData(@"C:\Users\Me\OneDrive")]
    [InlineData("OneDrive - Personal")]
    public void FindFilesystemTargetForScanTextResolvesCloudProviderTarget(string text)
    {
        var cloudTarget = new CloudProviderTarget(
            "onedrive",
            "OneDrive",
            "Personal",
            @"C:\Users\Me\OneDrive",
            @"Personal - C:\Users\Me\OneDrive",
            []);
        var row = TargetRow.FromCloudProvider(cloudTarget);

        var match = MainWindow.FindFilesystemTargetForScanText([row], [], text);

        Assert.NotNull(match);
        Assert.Equal(TargetKind.CloudProvider, match.Kind);
        Assert.Same(cloudTarget, match.CloudProviderTarget);
    }

    [Fact]
    public void CloudTargetInsertIndexPlacesCloudRowsAfterLocalDrives()
    {
        var recycleBin = TargetRow.RecycleBin();
        var drive = TargetRow.FromDrive(new DriveRow(@"C:\ System", @"C:\", "1 GB used", "2 GB free", 50));
        var portable = TargetRow.FromPortable(PortableDeviceTarget.Unavailable(
            "wpd:phone",
            "Pixel",
            "Unlock the phone and choose USB File Transfer mode."));

        var index = CloudTargetRowService.CloudTargetInsertIndex([recycleBin, drive, portable]);

        Assert.Equal(2, index);
    }

    [Theory]
    [InlineData("Google Drive", true)]
    [InlineData(" google drive ", true)]
    [InlineData("Library", false)]
    [InlineData("", false)]
    public void IsCloudDriveVolumeLabelRecognizesGoogleDrive(string volumeLabel, bool expected)
    {
        Assert.Equal(expected, CloudTargetRowService.IsCloudDriveVolumeLabel(volumeLabel));
    }

    [Fact]
    public void IsCloudFilteredTargetIncludesGoogleDriveRows()
    {
        var row = TargetRow.FromDrive(new DriveRow(
            @"G:\ Google Drive",
            @"G:\",
            "1 GB used",
            "2 GB free",
            50,
            IsCloudDrive: true));

        Assert.True(CloudTargetRowService.IsCloudFilteredTarget(row));
    }

    [Fact]
    public void GoogleDriveTargetRowsCarryCloudMetadata()
    {
        var row = TargetRow.FromDrive(new DriveRow(
            @"G:\ Google Drive",
            @"G:\",
            "1 GB used",
            "2 GB free",
            50,
            IsCloudDrive: true));

        Assert.NotNull(row.CloudProvider);
        Assert.Equal("google-drive", row.CloudProvider.ProviderId);
        Assert.Equal("Google Drive", row.CloudProvider.ProviderName);
        Assert.Equal(@"G:\", row.CloudProvider.RootPath);
    }

    [Fact]
    public void FindFilesystemTargetForScanTextPreservesGoogleDriveMetadata()
    {
        var drive = new DriveRow(
            @"G:\ Google Drive",
            @"G:\",
            "1 GB used",
            "2 GB free",
            50,
            IsCloudDrive: true);
        var row = TargetRow.FromDrive(drive);

        var match = MainWindow.FindFilesystemTargetForScanText([row], [drive], @"G:\ Google Drive");

        Assert.NotNull(match);
        Assert.Equal(TargetKind.Drive, match.Kind);
        Assert.NotNull(match.CloudProvider);
        Assert.Equal("google-drive", match.CloudProvider.ProviderId);
    }

    [Fact]
    public void CloudTargetRowServiceAddsAndRemovesVisibleCloudRows()
    {
        var localDrive = new DriveRow(@"C:\ System", @"C:\", "1 GB used", "2 GB free", 50);
        var googleDrive = new DriveRow(
            @"G:\ Google Drive",
            @"G:\",
            "1 GB used",
            "2 GB free",
            50,
            IsCloudDrive: true);
        var cloudTarget = new CloudProviderTarget(
            "onedrive",
            "OneDrive",
            "Personal",
            @"C:\Users\Me\OneDrive",
            @"Personal - C:\Users\Me\OneDrive",
            []);
        var targetRows = new List<TargetRow>
        {
            TargetRow.RecycleBin(),
            TargetRow.FromDrive(localDrive)
        };
        var recentPaths = new List<string>();

        CloudTargetRowService.AddVisibleCloudTargetRows(
            targetRows,
            [localDrive, googleDrive],
            [cloudTarget],
            _ => false,
            _ => false,
            recentPaths.Add);

        Assert.Equal(
            [TargetKind.RecycleBin, TargetKind.Drive, TargetKind.Drive, TargetKind.CloudProvider],
            targetRows.Select(row => row.Kind));
        Assert.Contains(targetRows, row => row.Kind == TargetKind.Drive && row.IsCloudDrive);
        Assert.Contains(targetRows, row => row.Kind == TargetKind.CloudProvider && row.CloudProviderTarget == cloudTarget);
        Assert.Equal([@"G:\", @"C:\Users\Me\OneDrive"], recentPaths);

        CloudTargetRowService.RemoveCloudTargetRows(targetRows);

        Assert.Equal([TargetKind.RecycleBin, TargetKind.Drive], targetRows.Select(row => row.Kind));
        Assert.Equal(@"C:\", targetRows[1].RootPath);
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
