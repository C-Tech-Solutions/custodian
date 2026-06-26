using Custodian.App.Services;

public sealed class DeviceChangeTargetRefreshServiceTests
{
    [Theory]
    [InlineData(DeviceChangeTargetRefreshService.DbtDeviceArrival)]
    [InlineData(DeviceChangeTargetRefreshService.DbtDeviceRemoveComplete)]
    [InlineData(DeviceChangeTargetRefreshService.DbtDeviceNodesChanged)]
    [InlineData(DeviceChangeTargetRefreshService.DbtConfigChanged)]
    public void ShouldRefreshTargetsAcceptsDeviceChangeEvents(int wParam)
    {
        Assert.True(DeviceChangeTargetRefreshService.ShouldRefreshTargets(
            DeviceChangeTargetRefreshService.WmDeviceChange,
            new IntPtr(wParam)));
    }

    [Theory]
    [InlineData(0x0200, DeviceChangeTargetRefreshService.DbtDeviceArrival)]
    [InlineData(DeviceChangeTargetRefreshService.WmDeviceChange, 0x8001)]
    [InlineData(DeviceChangeTargetRefreshService.WmDeviceChange, 0)]
    public void ShouldRefreshTargetsRejectsUnrelatedEvents(int message, int wParam)
    {
        Assert.False(DeviceChangeTargetRefreshService.ShouldRefreshTargets(
            message,
            new IntPtr(wParam)));
    }
}
