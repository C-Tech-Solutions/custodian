using Custodian.Core.Model;

namespace Custodian.Core.Scanning;

public sealed record PortableDeviceScanDescriptor(
    string SourceId,
    string RootPath,
    string DisplayRootPath,
    string DeviceId = "",
    string StorageObjectId = "",
    string DeviceName = "",
    string StorageName = "",
    string Engine = "Portable Device (MTP)")
{
    public ScanSourceKind SourceKind => ScanSourceKind.PortableDevice;
}
