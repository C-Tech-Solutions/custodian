using Custodian.Core.Model;

namespace Custodian.Core.Portable;

public sealed record PortableCopyPlan(
    IReadOnlyList<PortableCopyPlanItem> Items,
    IReadOnlyList<SkippedEntry> SkippedEntries);

public sealed record PortableCopyPlanItem(
    FileSystemEntry Entry,
    string RelativePath,
    string DestinationPath);
