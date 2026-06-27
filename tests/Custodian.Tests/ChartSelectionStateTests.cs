using Custodian.App.Services;
using Custodian.Core.Model;
using Custodian.Core.Presentation;

namespace Custodian.Tests;

public sealed class ChartSelectionStateTests
{
    [Fact]
    public void SingleSelectionReplacesExistingKeys()
    {
        var state = new ChartSelectionState();
        var alpha = Slice("alpha");
        var beta = Slice("beta");

        state.Toggle(alpha);
        state.SelectSingle(beta);

        Assert.Equal(["beta"], state.SourceKeys);
        Assert.Same(beta, state.PrimarySlice([alpha, beta]));
    }

    [Fact]
    public void ToggleAddsAndRemovesKeys()
    {
        var state = new ChartSelectionState();
        var alpha = Slice("alpha");
        var beta = Slice("beta");

        state.Toggle(alpha);
        state.Toggle(beta);
        state.Toggle(alpha);

        Assert.Equal(["beta"], state.SourceKeys);
        Assert.Same(beta, state.PrimarySlice([alpha, beta]));
    }

    [Fact]
    public void PruneRemovesDisappearedKeys()
    {
        var state = new ChartSelectionState();
        var alpha = Slice("alpha");
        var beta = Slice("beta");

        state.Toggle(alpha);
        state.Toggle(beta);
        state.PruneTo([beta]);

        Assert.Equal(["beta"], state.SourceKeys);
        Assert.Same(beta, state.PrimarySlice([beta]));
    }

    [Fact]
    public void ToggleRemovalFallsBackToPreviousSelectedKey()
    {
        var state = new ChartSelectionState();
        var alpha = Slice("alpha");
        var beta = Slice("beta");
        var gamma = Slice("gamma");

        state.Toggle(alpha);
        state.Toggle(beta);
        state.Toggle(gamma);
        state.Toggle(gamma);

        Assert.Equal(["alpha", "beta"], state.SourceKeys);
        Assert.Same(beta, state.PrimarySlice([alpha, beta, gamma]));
    }

    [Fact]
    public void SelectedSlicesFollowDatasetOrder()
    {
        var state = new ChartSelectionState();
        var alpha = Slice("alpha");
        var beta = Slice("beta");

        state.Toggle(beta);
        state.Toggle(alpha);

        Assert.Equal([alpha, beta], state.SelectedSlices([alpha, beta]));
    }

    [Fact]
    public void OtherSliceIsNotActionable()
    {
        var other = Slice("other", ChartSliceKind.Other);

        Assert.Empty(ChartSelectionState.ActionableSlices([other]));
    }

    [Fact]
    public void SelectionTextSummarizesSingleAndMultipleSelections()
    {
        var alpha = Slice("alpha", rawBytes: 10);
        var beta = Slice("beta", rawBytes: 20);

        Assert.Equal("Select a slice to locate it in the grid.", ChartSelectionState.SelectionText([]));
        Assert.Equal("alpha: 10 B (10.0%)", ChartSelectionState.SelectionText([alpha]));
        Assert.Equal("2 chart items selected - 30 B", ChartSelectionState.SelectionText([alpha, beta]));
    }

    private static ChartSlice Slice(
        string key,
        ChartSliceKind kind = ChartSliceKind.Entry,
        long rawBytes = 10)
        => new(
            key,
            "detail",
            $"{rawBytes} B",
            rawBytes,
            10,
            "10.0%",
            "#FFFFFF",
            kind,
            key,
            kind == ChartSliceKind.Entry ? new FileSystemEntry { Name = key, FullPath = $@"C:\Root\{key}.bin" } : null,
            key,
            ShowCallout: false,
            FileCategory.Other);
}
