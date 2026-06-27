using System.Windows.Input;
using Custodian.App.Services;

namespace Custodian.Tests;

public sealed class DetailSelectionDeleteShortcutServiceTests
{
    [Fact]
    public void DeleteMapsToRecycle()
    {
        var mode = DetailSelectionDeleteShortcutService.Resolve(Key.Delete, ModifierKeys.None);

        Assert.Equal(DetailSelectionDeleteMode.Recycle, mode);
    }

    [Fact]
    public void ShiftDeleteMapsToPermanentDelete()
    {
        var mode = DetailSelectionDeleteShortcutService.Resolve(Key.Delete, ModifierKeys.Shift);

        Assert.Equal(DetailSelectionDeleteMode.PermanentDelete, mode);
    }

    [Theory]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift)]
    public void UnsupportedModifiersAreIgnored(ModifierKeys modifiers)
    {
        Assert.Null(DetailSelectionDeleteShortcutService.Resolve(Key.Delete, modifiers));
    }

    [Fact]
    public void NonDeleteKeysAreIgnored()
    {
        Assert.Null(DetailSelectionDeleteShortcutService.Resolve(Key.Enter, ModifierKeys.None));
    }

    [Fact]
    public void ChartSelectionDeleteMapsToRecycle()
        => Assert.Equal(
            DetailSelectionDeleteMode.Recycle,
            DetailSelectionDeleteShortcutService.ResolveForChartSelection(Key.Delete, ModifierKeys.None));

    [Fact]
    public void ChartSelectionControlDeleteMapsToRecycle()
        => Assert.Equal(
            DetailSelectionDeleteMode.Recycle,
            DetailSelectionDeleteShortcutService.ResolveForChartSelection(Key.Delete, ModifierKeys.Control));

    [Fact]
    public void ChartSelectionShiftDeleteMapsToPermanentDelete()
        => Assert.Equal(
            DetailSelectionDeleteMode.PermanentDelete,
            DetailSelectionDeleteShortcutService.ResolveForChartSelection(Key.Delete, ModifierKeys.Shift));

    [Fact]
    public void ChartSelectionControlShiftDeleteMapsToPermanentDelete()
        => Assert.Equal(
            DetailSelectionDeleteMode.PermanentDelete,
            DetailSelectionDeleteShortcutService.ResolveForChartSelection(
                Key.Delete,
                ModifierKeys.Control | ModifierKeys.Shift));

    [Theory]
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Windows)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt)]
    public void ChartSelectionIgnoresUnsupportedModifiers(ModifierKeys modifiers)
    {
        Assert.Null(DetailSelectionDeleteShortcutService.ResolveForChartSelection(Key.Delete, modifiers));
    }

    [Fact]
    public void ChartSelectionIgnoresNonDeleteKeys()
    {
        Assert.Null(DetailSelectionDeleteShortcutService.ResolveForChartSelection(Key.Enter, ModifierKeys.Control));
    }
}
