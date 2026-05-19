using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

public sealed record ChartSlice(
    string Label,
    string Detail,
    string FormattedSize,
    long RawBytes,
    double Percent,
    string PercentText,
    string Color,
    ChartSliceKind Kind,
    string SourceKey,
    FileSystemEntry? Entry,
    string ShortLabel,
    bool ShowCallout,
    FileCategory Category);
