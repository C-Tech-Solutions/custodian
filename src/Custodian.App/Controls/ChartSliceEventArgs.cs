using Custodian.Core.Presentation;

namespace Custodian.App.Controls;

public sealed class ChartSliceEventArgs(ChartSlice slice) : EventArgs
{
    public ChartSlice Slice { get; } = slice;
}
