using System.IO.Packaging;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Custodian.Tests;

public sealed class ThemeDisabledStateTests
{
    private const int Width = 160;
    private const int Height = 120;

    // The main window disables itself while it relaunches as administrator and while it
    // closes. The stock Windows templates for these controls paint white or system-gray
    // panels when disabled, which showed as unthemed blocks during the relaunch.
    [Theory]
    [InlineData("Dark", typeof(ListBox))]
    [InlineData("Dark", typeof(ListView))]
    [InlineData("Dark", typeof(TreeView))]
    [InlineData("Light", typeof(ListBox))]
    [InlineData("Light", typeof(ListView))]
    [InlineData("Light", typeof(TreeView))]
    public void DisabledListControlRendersLikeEnabled(string palette, Type controlType)
    {
        RunInSta(() =>
        {
            var enabled = Render(palette, controlType, isEnabled: true);
            var disabled = Render(palette, controlType, isEnabled: false);

            Assert.True(
                enabled.AsSpan().SequenceEqual(disabled),
                $"A disabled {controlType.Name} should keep the {palette} theme's look.");
        });
    }

    private static byte[] Render(string palette, Type controlType, bool isEnabled)
    {
        var control = (Control)Activator.CreateInstance(controlType)!;
        var host = new Border { Child = control, IsEnabled = isEnabled };
        host.Resources.MergedDictionaries.Add(LoadThemeDictionary($"Palette.{palette}.xaml"));
        host.Resources.MergedDictionaries.Add(LoadThemeDictionary("Controls.xaml"));
        host.SetResourceReference(Border.BackgroundProperty, "Surface");

        var size = new Size(Width, Height);
        host.Measure(size);
        host.Arrange(new Rect(size));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);
        return pixels;
    }

    private static ResourceDictionary LoadThemeDictionary(string fileName)
    {
        // Touching PackUriHelper registers the pack:// scheme when no Application exists.
        _ = PackUriHelper.UriSchemePack;
        return new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Custodian.App;component/Themes/{fileName}", UriKind.Absolute)
        };
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
