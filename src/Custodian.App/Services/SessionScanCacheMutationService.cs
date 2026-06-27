namespace Custodian.App.Services;

internal static class SessionScanCacheMutationService
{
    internal static SessionScanCacheRemoval<TScan> Remove<TScan>(
        string rootPath,
        IDictionary<string, TScan> scanCache,
        LinkedList<string> scanCacheOrder,
        string? visibleScanKey,
        Func<string, string?> resolveCacheKey)
        where TScan : class
    {
        ArgumentNullException.ThrowIfNull(scanCache);
        ArgumentNullException.ThrowIfNull(scanCacheOrder);
        ArgumentNullException.ThrowIfNull(resolveCacheKey);

        var key = resolveCacheKey(rootPath);
        if (string.IsNullOrWhiteSpace(key))
        {
            return new SessionScanCacheRemoval<TScan>(null, null, RemovedVisibleScan: false);
        }

        RemoveFromOrder(scanCacheOrder, key);
        scanCache.Remove(key, out var removedScan);

        return new SessionScanCacheRemoval<TScan>(
            key,
            removedScan,
            string.Equals(visibleScanKey, key, StringComparison.OrdinalIgnoreCase));
    }

    private static void RemoveFromOrder(LinkedList<string> scanCacheOrder, string key)
    {
        for (var node = scanCacheOrder.First; node is not null; node = node.Next)
        {
            if (string.Equals(node.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                scanCacheOrder.Remove(node);
                return;
            }
        }
    }
}

internal sealed record SessionScanCacheRemoval<TScan>(string? CacheKey, TScan? RemovedScan, bool RemovedVisibleScan)
    where TScan : class;
