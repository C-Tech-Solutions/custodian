namespace Custodian.Platform.Windows.Services;

public sealed record PortableDeviceTarget(
    string TargetId,
    string DeviceId,
    string DeviceName,
    string StorageObjectId,
    string StorageName,
    string DisplayPath,
    long? CapacityBytes,
    long? FreeBytes,
    bool IsAvailable,
    string DetailText)
{
    public static PortableDeviceTarget Unavailable(string deviceId, string deviceName, string detailText)
        => new(
            PortableDeviceService.BuildUnavailableTargetId(deviceId),
            deviceId,
            deviceName,
            string.Empty,
            string.Empty,
            deviceName,
            null,
            null,
            IsAvailable: false,
            detailText);
}
