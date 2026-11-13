namespace Custodian.Core.Model;

/// <param name="UseDirectMftRead">
/// MFT scans read the <c>$MFT</c> straight from the volume device, and fall back to <c>FSCTL_ENUM_USN_DATA</c>
/// when that fails. False uses the enumeration only.
/// </param>
public sealed record ScanOptions(
    string RootPath,
    ScanMode Mode = ScanMode.Auto,
    bool FollowReparsePoints = false,
    bool IncludeHiddenAndSystem = true,
    bool CollectAllocatedSize = false,
    CloudProviderMetadata? CloudProvider = null,
    bool UseDirectMftRead = true);
