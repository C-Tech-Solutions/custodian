using System.Windows.Input;

namespace Custodian.App.Services;

internal sealed class MouseHistoryNavigationService(
    Func<bool> canGoBack,
    Action goBack,
    Func<bool> canGoForward,
    Action goForward)
{
    public void HandlePreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1 && canGoBack())
        {
            e.Handled = true;
            goBack();
        }
        else if (e.ChangedButton == MouseButton.XButton2 && canGoForward())
        {
            e.Handled = true;
            goForward();
        }
    }
}
