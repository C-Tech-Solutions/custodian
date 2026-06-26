namespace Custodian.App.Services;

internal enum TargetRefreshReason
{
    Startup,
    Manual,
    DeviceChange
}

internal sealed class TargetRefreshCoordinator(Func<TargetRefreshReason, Task> refreshAsync)
{
    private bool _isRefreshing;
    private bool _hasQueuedRefresh;

    public bool IsRefreshing => _isRefreshing;

    public bool HasQueuedRefresh => _hasQueuedRefresh;

    public async Task RequestRefreshAsync(TargetRefreshReason reason)
    {
        if (_isRefreshing)
        {
            _hasQueuedRefresh = true;
            return;
        }

        _isRefreshing = true;
        try
        {
            var currentReason = reason;
            do
            {
                _hasQueuedRefresh = false;
                await refreshAsync(currentReason);
                currentReason = TargetRefreshReason.DeviceChange;
            }
            while (_hasQueuedRefresh);
        }
        finally
        {
            _isRefreshing = false;
        }
    }
}
