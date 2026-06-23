using Custodian.App.Services;
using Vanara.PInvoke;
using PortableObjectProperties = Custodian.App.Services.PortableDeviceService.PortableObjectProperties;

public sealed class PortableDeviceServiceTests
{
    private const string DeviceRoot = "DEVICE";
    private const string UsbPhoneId = @"USB\VID_04E8&PID_6860&MS_COMP_MTP&SAMSUNG_ANDROID\PHONE";
    private const string LocalProjectionId = @"SWD\WPDBUSENUM\{657BF548-8DCB-11EE-99AA-806E6F6E6963}#0000000000100000";

    [Fact]
    public void BuildTargetsForDeviceIgnoresLocalVolumeProjectionWithoutStorage()
    {
        var labels = PortableDeviceService.CreateLocalVolumeLabelSet(["Library"]);

        var targets = PortableDeviceService.BuildTargetsForDevice(LocalProjectionId, "Library", [], labels);

        Assert.Empty(targets);
        Assert.True(PortableDeviceService.IsLocalVolumeProjection(LocalProjectionId, @"D:\ Library", labels, hasReadableStorage: false));
        Assert.False(PortableDeviceService.IsLocalVolumeProjection(LocalProjectionId, "Library", labels, hasReadableStorage: true));
    }

    [Fact]
    public void BuildTargetsForDeviceCreatesUnavailablePhoneTargetWithoutStorage()
    {
        var labels = PortableDeviceService.CreateLocalVolumeLabelSet(["Library"]);

        var targets = PortableDeviceService.BuildTargetsForDevice(UsbPhoneId, "Colten's S23 Ultra", [], labels);

        var target = Assert.Single(targets);
        Assert.False(target.IsAvailable);
        Assert.Equal("Colten's S23 Ultra", target.DeviceName);
        Assert.Contains("File Transfer", target.DetailText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscoverStorageRootsFindsDirectStorageObject()
    {
        var records = new Dictionary<string, PortableObjectProperties>
        {
            ["storage"] = Storage("Internal shared storage", capacity: 100, free: 25)
        };
        var children = new Dictionary<string, IReadOnlyList<string>>
        {
            [DeviceRoot] = ["storage"]
        };

        var roots = PortableDeviceService.DiscoverStorageRoots(
            DeviceRoot,
            parent => children.GetValueOrDefault(parent, []),
            objectId => records[objectId],
            CancellationToken.None);
        var targets = PortableDeviceService.BuildTargetsForDevice(UsbPhoneId, "Colten's S23 Ultra", roots, EmptyLabels());

        var target = Assert.Single(targets);
        Assert.True(target.IsAvailable);
        Assert.Equal("storage", target.StorageObjectId);
        Assert.Equal("Internal shared storage", target.StorageName);
        Assert.Equal("Colten's S23 Ultra/Internal shared storage", target.DisplayPath);
        Assert.Equal(100, target.CapacityBytes);
        Assert.Equal(25, target.FreeBytes);
    }

    [Fact]
    public void DiscoverStorageRootsFindsNestedStorageObject()
    {
        var records = new Dictionary<string, PortableObjectProperties>
        {
            ["phone"] = Folder("Phone"),
            ["storage"] = Storage("Internal storage")
        };
        var children = new Dictionary<string, IReadOnlyList<string>>
        {
            [DeviceRoot] = ["phone"],
            ["phone"] = ["storage"]
        };

        var roots = PortableDeviceService.DiscoverStorageRoots(
            DeviceRoot,
            parent => children.GetValueOrDefault(parent, []),
            objectId => records[objectId],
            CancellationToken.None);
        var targets = PortableDeviceService.BuildTargetsForDevice(UsbPhoneId, "Colten's S23 Ultra", roots, EmptyLabels());

        var target = Assert.Single(targets);
        Assert.True(target.IsAvailable);
        Assert.Equal("storage", target.StorageObjectId);
        Assert.Equal("Internal storage", target.StorageName);
    }

    [Fact]
    public void DiscoverStorageRootsFindsMixedDirectAndNestedStorageObjects()
    {
        var records = new Dictionary<string, PortableObjectProperties>
        {
            ["internal"] = Storage("Internal storage"),
            ["phone"] = Folder("Phone"),
            ["sd-card"] = Storage("SD card")
        };
        var children = new Dictionary<string, IReadOnlyList<string>>
        {
            [DeviceRoot] = ["internal", "phone"],
            ["phone"] = ["sd-card"]
        };

        var roots = PortableDeviceService.DiscoverStorageRoots(
            DeviceRoot,
            parent => children.GetValueOrDefault(parent, []),
            objectId => records[objectId],
            CancellationToken.None);

        Assert.Equal(["internal", "sd-card"], roots.Select(root => root.ObjectId));
    }

    [Fact]
    public void BuildTargetsForDeviceUsesStoragePersistentIdForStableTargetId()
    {
        var first = PortableDeviceService.BuildTargetsForDevice(
            UsbPhoneId,
            "Colten's S23 Ultra",
            [new PortableDeviceService.PortableStorageObject("storage-v1", Storage("Internal storage", persistentId: "persistent-storage"))],
            EmptyLabels());
        var second = PortableDeviceService.BuildTargetsForDevice(
            UsbPhoneId,
            "Colten's S23 Ultra",
            [new PortableDeviceService.PortableStorageObject("storage-v2", Storage("Internal storage", persistentId: "persistent-storage"))],
            EmptyLabels());

        Assert.Equal(first[0].TargetId, second[0].TargetId);
        Assert.Equal("storage-v1", first[0].StorageObjectId);
        Assert.Equal("storage-v2", second[0].StorageObjectId);
    }

    [Fact]
    public void BuildTargetsForDeviceFallsBackToStorageNameForStableTargetId()
    {
        var first = PortableDeviceService.BuildTargetsForDevice(
            UsbPhoneId,
            "Colten's S23 Ultra",
            [new PortableDeviceService.PortableStorageObject("storage-v1", Storage("Internal storage"))],
            EmptyLabels());
        var second = PortableDeviceService.BuildTargetsForDevice(
            UsbPhoneId,
            "Colten's S23 Ultra",
            [new PortableDeviceService.PortableStorageObject("storage-v2", Storage("Internal storage"))],
            EmptyLabels());

        Assert.Equal(first[0].TargetId, second[0].TargetId);
        Assert.Equal("storage-v1", first[0].StorageObjectId);
        Assert.Equal("storage-v2", second[0].StorageObjectId);
    }

    [Fact]
    public void BuildTargetsForDeviceDisambiguatesDuplicateStorageNameFallbackIds()
    {
        var targets = PortableDeviceService.BuildTargetsForDevice(
            UsbPhoneId,
            "Colten's S23 Ultra",
            [
                new PortableDeviceService.PortableStorageObject("storage-a", Storage("Portable storage")),
                new PortableDeviceService.PortableStorageObject("storage-b", Storage("Portable storage"))
            ],
            EmptyLabels());

        Assert.Equal(2, targets.Count);
        Assert.NotEqual(targets[0].TargetId, targets[1].TargetId);
        Assert.Equal("storage-a", targets[0].StorageObjectId);
        Assert.Equal("storage-b", targets[1].StorageObjectId);
    }

    [Fact]
    public void BuildTargetsForDeviceKeepsDuplicateStorageFallbackIdsStableAcrossEnumerationOrder()
    {
        var firstOrder = PortableDeviceService.BuildTargetsForDevice(
            UsbPhoneId,
            "Colten's S23 Ultra",
            [
                new PortableDeviceService.PortableStorageObject("storage-a", Storage("Portable storage")),
                new PortableDeviceService.PortableStorageObject("storage-b", Storage("Portable storage"))
            ],
            EmptyLabels());
        var secondOrder = PortableDeviceService.BuildTargetsForDevice(
            UsbPhoneId,
            "Colten's S23 Ultra",
            [
                new PortableDeviceService.PortableStorageObject("storage-b", Storage("Portable storage")),
                new PortableDeviceService.PortableStorageObject("storage-a", Storage("Portable storage"))
            ],
            EmptyLabels());

        Assert.Equal(
            firstOrder.Single(target => target.StorageObjectId == "storage-a").TargetId,
            secondOrder.Single(target => target.StorageObjectId == "storage-a").TargetId);
        Assert.Equal(
            firstOrder.Single(target => target.StorageObjectId == "storage-b").TargetId,
            secondOrder.Single(target => target.StorageObjectId == "storage-b").TargetId);
    }

    private static IReadOnlySet<string> EmptyLabels()
        => PortableDeviceService.CreateLocalVolumeLabelSet([]);

    private static PortableObjectProperties Folder(string name)
        => new(
            name,
            null,
            PortableDeviceApi.WPD_CONTENT_TYPE_FUNCTIONAL_OBJECT,
            null,
            null,
            null,
            null,
            "PortableDevice",
            null,
            null,
            null);

    private static PortableObjectProperties Storage(
        string name,
        ulong? capacity = null,
        ulong? free = null,
        string? persistentId = null)
        => new(
            name,
            null,
            null,
            PortableDeviceApi.WPD_FUNCTIONAL_CATEGORY_STORAGE,
            persistentId,
            null,
            null,
            "PortableDevice",
            name,
            capacity,
            free);
}
