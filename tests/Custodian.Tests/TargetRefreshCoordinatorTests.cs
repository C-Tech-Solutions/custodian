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
        coordinator = new TargetRefreshCoordinator(async reason =>
        {
            calls++;
            if (calls == 1)
            {
                _ = coordinator!.RequestRefreshAsync(TargetRefreshReason.DeviceChange);
                _ = coordinator.RequestRefreshAsync(TargetRefreshReason.DeviceChange);
            }

            await Task.Yield();
        });

        await coordinator.RequestRefreshAsync(TargetRefreshReason.Manual);

        Assert.Equal(2, calls);
        Assert.False(coordinator.IsRefreshing);
        Assert.False(coordinator.HasQueuedRefresh);
    }

    [Fact]
    public async Task QueuedRefreshTaskCompletesOnlyAfterQueuedRefreshRuns()
    {
        TargetRefreshCoordinator? coordinator = null;
        var calls = 0;
        var firstRefreshCanComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator = new TargetRefreshCoordinator(async _ =>
        {
            calls++;
            if (calls == 1)
            {
                await firstRefreshCanComplete.Task;
            }
        });

        var firstTask = coordinator.RequestRefreshAsync(TargetRefreshReason.Manual);
        var queuedTask = coordinator.RequestRefreshAsync(TargetRefreshReason.DeviceChange);

        Assert.False(queuedTask.IsCompleted);

        firstRefreshCanComplete.SetResult();
        await firstTask;
        await queuedTask;

        Assert.Equal(2, calls);
    }
}
