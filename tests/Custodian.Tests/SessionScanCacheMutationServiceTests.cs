using Custodian.App.Services;

namespace Custodian.Tests;

public sealed class SessionScanCacheMutationServiceTests
{
    [Fact]
    public void RemoveEvictsCacheAndOrderEntry()
    {
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\"] = "scan",
            [@"D:\"] = "other"
        };
        var order = new LinkedList<string>([@"D:\", @"C:\"]);

        var removal = SessionScanCacheMutationService.Remove(@"c:\", cache, order, visibleScanKey: null, ResolveRoot);

        Assert.Equal(@"C:\", removal.CacheKey);
        Assert.Equal("scan", removal.RemovedScan);
        Assert.False(removal.RemovedVisibleScan);
        Assert.DoesNotContain(@"C:\", cache.Keys);
        Assert.Equal([@"D:\"], order);
    }

    [Fact]
    public void RemoveReportsVisibleScanInvalidationEvenWhenCacheEntryIsMissing()
    {
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new LinkedList<string>();

        var removal = SessionScanCacheMutationService.Remove(@"C:\", cache, order, visibleScanKey: @"C:\", ResolveRoot);

        Assert.Null(removal.RemovedScan);
        Assert.True(removal.RemovedVisibleScan);
    }

    [Fact]
    public void RemoveNoOpsWhenRootCannotBeNormalized()
    {
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"C:\"] = "scan" };
        var order = new LinkedList<string>([@"C:\"]);

        var removal = SessionScanCacheMutationService.Remove(@"bad", cache, order, visibleScanKey: @"C:\", _ => null);

        Assert.Null(removal.CacheKey);
        Assert.Null(removal.RemovedScan);
        Assert.False(removal.RemovedVisibleScan);
        Assert.Equal(["scan"], cache.Values);
        Assert.Equal([@"C:\"], order);
    }

    private static string? ResolveRoot(string rootPath)
        => string.Equals(rootPath, @"c:\", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(rootPath, @"C:\", StringComparison.OrdinalIgnoreCase)
            ? @"C:\"
            : rootPath;
}
