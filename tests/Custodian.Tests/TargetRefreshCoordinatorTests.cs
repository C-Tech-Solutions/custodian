using Custodian.App.Services;

public sealed class TargetRefreshCoordinatorTests
{
    [Fact]
    public async Task RequestRefreshRunsSingleRefreshWhenIdle()
    {
        var calls = 0;
        var coordinator = new TargetRefreshCoordinator(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await coordinator.RequestRefreshAsync(TargetRefreshReason.Manual);

        Assert.Equal(1, calls);
        Assert.False(coordinator.IsRefreshing);
        Assert.False(coordinator.HasQueuedRefresh);
    }

    [Fact]
    public async Task RequestRefreshDuringActiveRefreshQueuesOneFollowUp()
    {
        TargetRefreshCoordinator? coordinator = null;
        var calls = 0;
        coordinator = new TargetRefreshCoordinator(async _ =>
        {
            calls++;
            if (calls == 1)
            {
                await coordinator!.RequestRefreshAsync(TargetRefreshReason.DeviceChange);
                await coordinator.RequestRefreshAsync(TargetRefreshReason.DeviceChange);
            }
        });

        await coordinator.RequestRefreshAsync(TargetRefreshReason.Manual);

        Assert.Equal(2, calls);
        Assert.False(coordinator.IsRefreshing);
        Assert.False(coordinator.HasQueuedRefresh);
    }
}
