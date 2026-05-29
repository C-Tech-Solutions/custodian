using System.Windows;
namespace Custodian.App;

public partial class UpdateDialog : Window
{
    public UpdateDialog()
    {
        InitializeComponent();
    }

    public static bool ShowConfirmation(
        Window owner,
        string title,
        string message,
        string primaryText,
        string secondaryText,
        UpdateDialogTone tone = UpdateDialogTone.Information,
        string subtitle = "Custodian updater")
    {
        var dialog = Create(owner, title, subtitle, message, primaryText, secondaryText, tone);
        return dialog.ShowDialog() == true;
    }

    public static void ShowInformation(
        Window owner,
        string title,
        string message,
        UpdateDialogTone tone = UpdateDialogTone.Information,
        string subtitle = "Custodian updater")
    {
        var dialog = Create(owner, title, subtitle, message, "OK", string.Empty, tone);
        dialog.SecondaryButton.Visibility = Visibility.Collapsed;
        dialog.ShowDialog();
    }

    private static UpdateDialog Create(
        Window owner,
        string title,
        string subtitle,
        string message,
        string primaryText,
        string secondaryText,
        UpdateDialogTone tone)
    {
        var dialog = new UpdateDialog
        {
            Owner = owner
        };

        dialog.TitleText.Text = title;
        dialog.SubtitleText.Text = subtitle;
        dialog.MessageText.Text = message;
        dialog.PrimaryButton.Content = primaryText;
        dialog.SecondaryButton.Content = secondaryText;
        dialog.ApplyTone(tone);
        return dialog;
    }

    private void ApplyTone(UpdateDialogTone tone)
    {
        var brushKey = tone switch
        {
            UpdateDialogTone.Success => "SuccessBrush",
            UpdateDialogTone.Warning => "WarningBrush",
            UpdateDialogTone.Error => "DangerBrush",
            _ => "AccentBrush"
        };

        var icon = tone switch
        {
            UpdateDialogTone.Success => "\uE930",
            UpdateDialogTone.Warning => "\uE7BA",
            UpdateDialogTone.Error => "\uE783",
            _ => "\uE895"
        };

        IconText.Text = icon;
        if (TryFindResource(brushKey) is System.Windows.Media.Brush brush)
        {
            IconText.Foreground = brush;
            IconBadge.BorderBrush = brush;
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

public enum UpdateDialogTone
{
    Information,
    Success,
    Warning,
    Error
}
