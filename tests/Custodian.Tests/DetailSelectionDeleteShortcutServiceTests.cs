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
}
