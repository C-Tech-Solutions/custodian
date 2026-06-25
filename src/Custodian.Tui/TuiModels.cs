using Custodian.Core.Model;
using Custodian.Core.Presentation;
using Custodian.Platform.Windows.Services;

namespace Custodian.Tui;

internal enum DetailMode
{
    Contents,
    LargestFiles,
    LargestFolders,
    Extensions
}

internal enum ChartScope
{
    SelectedFolder,
    LargestFolders,
    LargestFiles,
    Extensions
}

internal sealed record TargetLine(string Label, string Path, PortableDeviceTarget? PortableTarget)
{
    public bool IsPortable => PortableTarget is not null;
    public override string ToString() => Label;
}

internal sealed record DetailLine(DetailRow Row)
{
    public override string ToString()
        => $"{Row.Icon} {Row.Name,-34} {Row.Kind,-10} {Row.LogicalSize,12} {Row.PercentText,7} {Row.FullPath}";
}

internal sealed record RecycleLine(RecycleBinRow Row)
{
    public override string ToString()
        => $"{Row.Name,-34} {Row.Size,12} {Row.DateDeleted,-20} {Row.OriginalLocation}";
}
