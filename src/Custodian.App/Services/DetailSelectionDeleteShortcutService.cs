using System.Windows.Input;

namespace Custodian.App.Services;

internal static class DetailSelectionDeleteShortcutService
{
    internal static DetailSelectionDeleteMode? Resolve(Key key, ModifierKeys modifiers)
    {
        if (key != Key.Delete)
        {
            return null;
        }

        return modifiers switch
        {
            ModifierKeys.None => DetailSelectionDeleteMode.Recycle,
            ModifierKeys.Shift => DetailSelectionDeleteMode.PermanentDelete,
            _ => null
        };
    }
}
