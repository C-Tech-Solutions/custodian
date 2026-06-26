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
        Task? firstQueuedTask = null;
        Task? secondQueuedTask = null;
        coordinator = new TargetRefreshCoordinator(async reason =>
        {
            calls++;
            if (calls == 1)
            {
                firstQueuedTask = coordinator!.RequestRefreshAsync(TargetRefreshReason.DeviceChange);
                secondQueuedTask = coordinator.RequestRefreshAsync(TargetRefreshReason.DeviceChange);
            }

            await Task.Yield();
        });

        await coordinator.RequestRefreshAsync(TargetRefreshReason.Manual);
        await firstQueuedTask!;
        await secondQueuedTask!;

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

    [Fact]
    public async Task InitialRefreshTaskCompletesBeforeQueuedRefreshFinishes()
    {
        var calls = 0;
        var firstRefreshCanComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedRefreshCanComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new TargetRefreshCoordinator(async _ =>
        {
            calls++;
            if (calls == 1)
            {
                await firstRefreshCanComplete.Task;
                return;
            }

            await queuedRefreshCanComplete.Task;
        });

        var firstTask = coordinator.RequestRefreshAsync(TargetRefreshReason.Manual);
        var queuedTask = coordinator.RequestRefreshAsync(TargetRefreshReason.DeviceChange);

        firstRefreshCanComplete.SetResult();
        await firstTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, calls);
        Assert.False(queuedTask.IsCompleted);
        Assert.True(coordinator.IsRefreshing);

        queuedRefreshCanComplete.SetResult();
        await queuedTask;

        Assert.False(coordinator.IsRefreshing);
        Assert.False(coordinator.HasQueuedRefresh);
    }

    [Fact]
    public async Task QueuedRefreshFailureDoesNotFaultCompletedInitialRefreshTask()
    {
        var calls = 0;
        var expected = new InvalidOperationException("Queued refresh failed.");
        var coordinator = new TargetRefreshCoordinator(_ =>
        {
            calls++;
            return calls == 1
                ? Task.CompletedTask
                : Task.FromException(expected);
        });

        var firstTask = coordinator.RequestRefreshAsync(TargetRefreshReason.Manual);
        var queuedTask = coordinator.RequestRefreshAsync(TargetRefreshReason.DeviceChange);

        await firstTask.WaitAsync(TimeSpan.FromSeconds(1));
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => queuedTask);

        Assert.Same(expected, actual);
        Assert.True(firstTask.IsCompletedSuccessfully);
        Assert.False(coordinator.IsRefreshing);
        Assert.False(coordinator.HasQueuedRefresh);
    }
}
