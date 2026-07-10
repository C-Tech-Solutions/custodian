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
    public void FindFilesystemTargetForScanTextResolvesNextcloudTargetAndMetadata()
    {
        var cloudTarget = new CloudProviderTarget(
            "nextcloud",
            "Nextcloud",
            "cloud.example.test",
            @"C:\Users\Me\Nextcloud",
            @"cloud.example.test - C:\Users\Me\Nextcloud",
            []);
        var row = TargetRow.FromCloudProvider(cloudTarget);

        var match = MainWindow.FindFilesystemTargetForScanText([row], [], @"C:\Users\Me\Nextcloud");

        Assert.NotNull(match);
        Assert.Equal(TargetKind.CloudProvider, match.Kind);
        Assert.Same(cloudTarget, match.CloudProviderTarget);
        Assert.NotNull(match.CloudProvider);
        Assert.Equal("nextcloud", match.CloudProvider.ProviderId);
        Assert.Equal("Nextcloud", match.CloudProvider.ProviderName);
        Assert.Equal("cloud.example.test", match.CloudProvider.AccountLabel);
        Assert.Equal(@"C:\Users\Me\Nextcloud", match.CloudProvider.RootPath);
    }

    [Fact]
    public void FindFilesystemTargetForScanTextResolvesDropboxTargetAndMetadata()
    {
        var cloudTarget = new CloudProviderTarget(
            "dropbox",
            "Dropbox",
            "Business",
            @"C:\Users\Me\Dropbox (Acme)",
            @"Business - C:\Users\Me\Dropbox (Acme)",
            []);
        var row = TargetRow.FromCloudProvider(cloudTarget);

        var match = MainWindow.FindFilesystemTargetForScanText([row], [], @"C:\Users\Me\Dropbox (Acme)");

        Assert.NotNull(match);
        Assert.Equal(TargetKind.CloudProvider, match.Kind);
        Assert.Same(cloudTarget, match.CloudProviderTarget);
        Assert.NotNull(match.CloudProvider);
        Assert.Equal("dropbox", match.CloudProvider.ProviderId);
        Assert.Equal("Dropbox", match.CloudProvider.ProviderName);
        Assert.Equal("Business", match.CloudProvider.AccountLabel);
        Assert.Equal(@"C:\Users\Me\Dropbox (Acme)", match.CloudProvider.RootPath);
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
    public void PortableTargetCanUseCachedScanMatchesRefreshedIPhoneStorageIdentity()
    {
        var target = new PortableDeviceTarget(
            "wpd:current-iphone-storage",
            "iphone-device-id",
            "Cam's iPhone",
            "current-storage-object-id",
            "Internal Storage",
            "Cam's iPhone/Internal Storage",
            null,
            null,
            IsAvailable: true,
            "Portable device storage");
        var scan = new ScanResult
        {
            RootPath = "wpd:stale-iphone-storage",
            SourceId = "wpd:stale-iphone-storage",
            SourceKind = ScanSourceKind.PortableDevice,
            DisplayRootPath = "Cam's iPhone/Internal Storage",
            PortableDeviceId = "iphone-device-id",
            PortableStorageObjectId = "stale-storage-object-id",
            PortableStorageName = "Internal Storage"
        };

        Assert.True(MainWindow.PortableTargetCanUseCachedScan(target, scan));
    }

    [Fact]
    public void PortableTargetCanUseCachedScanRejectsNonPortableScan()
    {
        var target = new PortableDeviceTarget(
            "wpd:current-iphone-storage",
            "iphone-device-id",
            "Cam's iPhone",
            "current-storage-object-id",
            "Internal Storage",
            "Cam's iPhone/Internal Storage",
            null,
            null,
            IsAvailable: true,
            "Portable device storage");
        var scan = new ScanResult
        {
            RootPath = @"C:\",
            SourceKind = ScanSourceKind.FileSystem,
            DisplayRootPath = @"C:\",
            PortableDeviceId = "iphone-device-id",
            PortableStorageName = "Internal Storage"
        };

        Assert.False(MainWindow.PortableTargetCanUseCachedScan(target, scan));
    }

    [Fact]
    public void PortableTargetMatchesActiveScanAfterWpdTargetIdChanges()
    {
        var activeTarget = new PortableDeviceTarget(
            "wpd:stale-iphone-storage",
            "iphone-device-id",
            "Cam's iPhone",
            "stale-storage-object-id",
            "Internal Storage",
            "Cam's iPhone/Internal Storage",
            null,
            null,
            IsAvailable: true,
            "Portable device storage");
        var refreshedTarget = activeTarget with
        {
            TargetId = "wpd:current-iphone-storage",
            StorageObjectId = "current-storage-object-id"
        };

        Assert.True(MainWindow.PortableTargetMatchesActiveScan(
            refreshedTarget,
            activeTarget,
            [refreshedTarget]));
    }

    [Fact]
    public void PortableTargetFallbackIdentityRejectsAmbiguousStorageNames()
    {
        var target = new PortableDeviceTarget(
            "wpd:first-storage",
            "iphone-device-id",
            "Cam's iPhone",
            "first-storage-object-id",
            "Internal Storage",
            "Cam's iPhone/Internal Storage",
            null,
            null,
            IsAvailable: true,
            "Portable device storage");
        var duplicate = target with
        {
            TargetId = "wpd:second-storage",
            StorageObjectId = "second-storage-object-id"
        };

        Assert.False(MainWindow.PortableTargetFallbackIdentityIsUnambiguous(
            [target, duplicate],
            target));
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
    public void FindEquivalentTargetRowMatchesDriveByRootPath()
    {
        var previous = TargetRow.FromDrive(new DriveRow(@"D:\ Media", @"D:\", "1 GB used", "2 GB free", 50));
        var current = TargetRow.FromDrive(new DriveRow(@"D:\ Renamed", @"D:\", "2 GB used", "2 GB free", 50));

        var match = TargetMatchingService.FindEquivalentTargetRow([current], previous);

        Assert.Same(current, match);
    }

    [Fact]
    public void FindEquivalentTargetRowMatchesUnavailablePhoneToAvailableStorage()
    {
        var previous = TargetRow.FromPortable(PortableDeviceTarget.Unavailable(
            "phone-device-id",
            "Pixel",
            "Unlock the phone and choose USB File Transfer mode."));
        var availableTarget = new PortableDeviceTarget(
            "wpd:stable-storage",
            "phone-device-id",
            "Pixel",
            "storage-object-id",
            "Internal storage",
            "Pixel/Internal storage",
            null,
            null,
            IsAvailable: true,
            "Portable device storage");
        var current = TargetRow.FromPortable(availableTarget);

        var match = TargetMatchingService.FindEquivalentTargetRow([current], previous);

        Assert.Same(current, match);
    }

    [Fact]
    public void FindEquivalentTargetRowMatchesAvailablePhoneWhenWpdStorageIdentityChanges()
    {
        var previousTarget = new PortableDeviceTarget(
            "wpd:stale-iphone-storage",
            "iphone-device-id",
            "Cam's iPhone",
            "stale-storage-object-id",
            "Internal Storage",
            "Cam's iPhone/Internal Storage",
            null,
            null,
            IsAvailable: true,
            "Portable device storage");
        var currentTarget = previousTarget with
        {
            TargetId = "wpd:current-iphone-storage",
            StorageObjectId = "current-storage-object-id"
        };
        var current = TargetRow.FromPortable(currentTarget);

        var match = TargetMatchingService.FindEquivalentTargetRow([current], TargetRow.FromPortable(previousTarget));

        Assert.Same(current, match);
    }

    [Fact]
    public void FindEquivalentTargetRowDoesNotPickArbitraryStorageForUnavailablePhone()
    {
        var previous = TargetRow.FromPortable(PortableDeviceTarget.Unavailable(
            "phone-device-id",
            "Pixel",
            "Unlock the phone and choose USB File Transfer mode."));
        var internalStorage = new PortableDeviceTarget(
            "wpd:internal",
            "phone-device-id",
            "Pixel",
            "internal-object-id",
            "Internal storage",
            "Pixel/Internal storage",
            null,
            null,
            IsAvailable: true,
            "Portable device storage");
        var sdCard = new PortableDeviceTarget(
            "wpd:sd-card",
            "phone-device-id",
            "Pixel",
            "sd-card-object-id",
            "SD card",
            "Pixel/SD card",
            null,
            null,
            IsAvailable: true,
            "Portable device storage");

        var match = TargetMatchingService.FindEquivalentTargetRow(
            [TargetRow.FromPortable(internalStorage), TargetRow.FromPortable(sdCard)],
            previous);

        Assert.Null(match);
    }

    [Theory]
    [InlineData("onedrive", "OneDrive", "Personal", @"C:\Users\Me\OneDrive")]
    [InlineData("nextcloud", "Nextcloud", "cloud.example.test", @"C:\Users\Me\Nextcloud")]
    [InlineData("dropbox", "Dropbox", "Business", @"C:\Users\Me\Dropbox (Acme)")]
    public void FindEquivalentTargetRowMatchesCloudProviderByProviderAndRoot(
        string providerId,
        string providerName,
        string accountLabel,
        string rootPath)
    {
        var previousTarget = new CloudProviderTarget(
            providerId,
            providerName,
            accountLabel,
            rootPath,
            $"{accountLabel} - {rootPath}",
            []);
        var currentTarget = previousTarget with
        {
            DetailText = $"{accountLabel} - {rootPath} - includes Desktop"
        };
        var current = TargetRow.FromCloudProvider(currentTarget);

        var match = TargetMatchingService.FindEquivalentTargetRow([current], TargetRow.FromCloudProvider(previousTarget));

        Assert.Same(current, match);
    }

    [Theory]
    [InlineData("onedrive", "OneDrive", "Personal", @"C:\Users\Me\OneDrive")]
    [InlineData("nextcloud", "Nextcloud", "cloud.example.test", @"C:\Users\Me\Nextcloud")]
    [InlineData("dropbox", "Dropbox", "Business", @"C:\Users\Me\Dropbox (Acme)")]
    public void FindEquivalentTargetRowReturnsNullWhenCloudRowsAreHidden(
        string providerId,
        string providerName,
        string accountLabel,
        string rootPath)
    {
        var previousTarget = new CloudProviderTarget(
            providerId,
            providerName,
            accountLabel,
            rootPath,
            $"{accountLabel} - {rootPath}",
            []);
        var localDrive = TargetRow.FromDrive(new DriveRow(@"C:\ System", @"C:\", "1 GB used", "2 GB free", 50));

        var match = TargetMatchingService.FindEquivalentTargetRow([TargetRow.RecycleBin(), localDrive], TargetRow.FromCloudProvider(previousTarget));

        Assert.Null(match);
    }

    [Fact]
    public void FindEquivalentTargetRowReturnsNullWhenRemovedTargetHasNoReplacement()
    {
        var previous = TargetRow.FromDrive(new DriveRow(@"E:\ Backup", @"E:\", "1 GB used", "2 GB free", 50));
        var current = TargetRow.FromDrive(new DriveRow(@"D:\ Media", @"D:\", "1 GB used", "2 GB free", 50));

        var match = TargetMatchingService.FindEquivalentTargetRow([current], previous);

        Assert.Null(match);
    }

    [Fact]
    public void FindEquivalentTargetRowMatchesAvailablePhoneToUnavailableDevice()
    {
        var previousTarget = new PortableDeviceTarget(
            "wpd:stable-storage",
            "phone-device-id",
            "Pixel",
            "storage-object-id",
            "Internal storage",
            "Pixel/Internal storage",
            null,
            null,
            IsAvailable: true,
            "Portable device storage");
        var unavailable = TargetRow.FromPortable(PortableDeviceTarget.Unavailable(
            "phone-device-id",
            "Pixel",
            "Unlock the phone and choose USB File Transfer mode."));

        var match = TargetMatchingService.FindEquivalentTargetRow([unavailable], TargetRow.FromPortable(previousTarget));

        Assert.Same(unavailable, match);
    }

    [Fact]
    public void FindEquivalentTargetRowDoesNotMatchDifferentAvailableStorageOnSamePhone()
    {
        var previousTarget = new PortableDeviceTarget(
            "wpd:sd-card",
            "phone-device-id",
            "Pixel",
            "sd-card-object-id",
            "SD card",
            "Pixel/SD card",
            null,
            null,
            IsAvailable: true,
            "Portable device storage");
        var internalStorage = new PortableDeviceTarget(
            "wpd:internal",
            "phone-device-id",
            "Pixel",
            "internal-object-id",
            "Internal storage",
            "Pixel/Internal storage",
            null,
            null,
            IsAvailable: true,
            "Portable device storage");

        var match = TargetMatchingService.FindEquivalentTargetRow(
            [TargetRow.FromPortable(internalStorage)],
            TargetRow.FromPortable(previousTarget));

        Assert.Null(match);
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
