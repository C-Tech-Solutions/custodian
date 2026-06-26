using Custodian.Core.Presentation;

namespace Custodian.App.Controls;

public sealed class ChartSliceEventArgs(ChartSlice slice, bool isToggleSelection = false) : EventArgs
{
    public ChartSlice Slice { get; } = slice;

    public bool IsToggleSelection { get; } = isToggleSelection;
}
