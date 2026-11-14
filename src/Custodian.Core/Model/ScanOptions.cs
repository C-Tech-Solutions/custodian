namespace Custodian.Core.Model;

/// <param name="MaxDegreeOfParallelism">
/// Upper bound on the directories the recursive engine lists at the same time.
/// <see langword="null"/> uses <c>min(processor count, 8)</c>; other values are clamped to
/// 1..<see cref="MaxDegreeOfParallelismLimit"/>. Cloud-provider scans always use 1.
/// </param>
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
    int? MaxDegreeOfParallelism = null,
    bool UseDirectMftRead = true)
{
    public const int MaxDegreeOfParallelismLimit = 64;
}
