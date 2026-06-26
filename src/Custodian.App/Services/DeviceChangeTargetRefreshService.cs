using System.Windows;
using System.Windows.Interop;

namespace Custodian.App.Services;

internal sealed class DeviceChangeTargetRefreshService(Action requestRefresh) : IDisposable
{
    internal const int WmDeviceChange = 0x0219;
    internal const int DbtDeviceNodesChanged = 0x0007;
    internal const int DbtConfigChanged = 0x0018;
    internal const int DbtDeviceArrival = 0x8000;
    internal const int DbtDeviceRemoveComplete = 0x8004;

    private HwndSource? _source;

    public void Attach(Window window)
    {
        Detach();
        _source = (HwndSource?)PresentationSource.FromVisual(window);
        _source?.AddHook(WndProc);
    }

    public void Detach()
    {
        if (_source is null)
        {
            return;
        }

        _source.RemoveHook(WndProc);
        _source = null;
    }

    public void Dispose()
        => Detach();

    internal static bool ShouldRefreshTargets(int message, IntPtr wParam)
        => message == WmDeviceChange &&
            wParam.ToInt64() is DbtDeviceNodesChanged or
                DbtConfigChanged or
                DbtDeviceArrival or
                DbtDeviceRemoveComplete;

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (ShouldRefreshTargets(message, wParam))
        {
            requestRefresh();
        }

        return IntPtr.Zero;
    }
}
