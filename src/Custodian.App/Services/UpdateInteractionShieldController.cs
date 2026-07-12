using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Custodian.App.Services;

internal sealed class UpdateInteractionShieldController(
    Border shield,
    TextBlock titleText,
    TextBlock detailText)
{
    private IInputElement? _focusBeforeBlock;

    public bool IsActive { get; private set; }

    public void Begin(string title, string detail)
    {
        if (!IsActive)
        {
            _focusBeforeBlock = Keyboard.FocusedElement;
        }

        IsActive = true;
        UpdateMessage(title, detail);
        shield.Visibility = Visibility.Visible;
        shield.Focus();
        Keyboard.Focus(shield);
    }

    public void UpdateMessage(string title, string detail)
    {
        titleText.Text = title;
        detailText.Text = detail;
    }

    public bool TryBlock(RoutedEventArgs eventArgs)
    {
        if (!IsActive)
        {
            return false;
        }

        eventArgs.Handled = true;
        return true;
    }

    public void End()
    {
        shield.Visibility = Visibility.Collapsed;
        IsActive = false;

        var focus = _focusBeforeBlock;
        _focusBeforeBlock = null;
        if (focus is not null)
        {
            Keyboard.Focus(focus);
        }
    }
}
