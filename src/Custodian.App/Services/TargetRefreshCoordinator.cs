namespace Custodian.App.Services;

internal enum TargetRefreshReason
{
    Startup,
    Manual,
    DeviceChange
}

internal sealed class TargetRefreshCoordinator(Func<TargetRefreshReason, Task> refreshAsync)
{
    private Task? _currentRefreshTask;
    private TaskCompletionSource? _queuedRefreshCompletion;
    private TargetRefreshReason _queuedReason = TargetRefreshReason.DeviceChange;

    public bool IsRefreshing => _currentRefreshTask is not null;

    public bool HasQueuedRefresh => _queuedRefreshCompletion is not null;

    public Task RequestRefreshAsync(TargetRefreshReason reason)
    {
        if (_currentRefreshTask is not null)
        {
            if (_queuedRefreshCompletion is null)
            {
                _queuedRefreshCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _queuedReason = reason;
            }
            else if (reason == TargetRefreshReason.Manual)
            {
                _queuedReason = TargetRefreshReason.Manual;
            }

            return _queuedRefreshCompletion.Task;
        }

        var currentRefreshCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _currentRefreshTask = currentRefreshCompletion.Task;
        _ = RunRefreshLoopAsync(reason, currentRefreshCompletion);
        return currentRefreshCompletion.Task;
    }

    private async Task RunRefreshLoopAsync(
        TargetRefreshReason initialReason,
        TaskCompletionSource currentRefreshCompletion)
    {
        var currentReason = initialReason;
        var activeCompletion = currentRefreshCompletion;
        try
        {
            while (true)
            {
                await refreshAsync(currentReason);

                if (_queuedRefreshCompletion is null)
                {
                    _currentRefreshTask = null;
                    activeCompletion.TrySetResult();
                    break;
                }

                activeCompletion.TrySetResult();
                currentReason = _queuedReason;
                activeCompletion = _queuedRefreshCompletion;
                _queuedRefreshCompletion = null;
                _queuedReason = TargetRefreshReason.DeviceChange;
            }
        }
        catch (Exception ex)
        {
            _currentRefreshTask = null;
            activeCompletion.TrySetException(ex);
            _queuedRefreshCompletion?.TrySetException(ex);
            _queuedRefreshCompletion = null;
        }
        finally
        {
            _currentRefreshTask = null;
        }
    }
}
