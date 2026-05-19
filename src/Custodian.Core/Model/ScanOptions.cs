namespace Custodian.Core.Model;

public sealed record ScanOptions(
    string RootPath,
    ScanMode Mode = ScanMode.Auto,
    bool FollowReparsePoints = false,
    bool IncludeHiddenAndSystem = true,
    bool CollectAllocatedSize = false);
