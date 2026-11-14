using Custodian.Core.Model;
using Custodian.Core.Scanning;

namespace Custodian.Tests;

public sealed class ProgressThrottleTests
{
    [Fact]
    public void ConcurrentCallersReportOncePerInterval()
    {
        var progress = new CountingProgress();
        var throttle = new ProgressThrottle(progress, TimeSpan.FromHours(1));
        using var start = new ManualResetEventSlim();

        var threads = Enumerable.Range(0, 16)
            .Select(_ => new Thread(() =>
            {
                start.Wait();
                for (var i = 0; i < 1_000; i++)
                {
                    throttle.Report("path", i, i, i, "Scanning folder");
                }
            }))
            .ToList();
        threads.ForEach(thread => thread.Start());
        start.Set();
        threads.ForEach(thread => thread.Join());

        Assert.Equal(1, progress.Count);
    }

    [Fact]
    public void ForcedReportsAlwaysPass()
    {
        var progress = new CountingProgress();
        var throttle = new ProgressThrottle(progress, TimeSpan.FromHours(1));

        throttle.Report("path", 1, 1, 1, "first");
        throttle.Report("path", 2, 2, 2, "throttled");
        throttle.Report("path", 3, 3, 3, "forced", force: true);

        Assert.Equal(2, progress.Count);
    }

    [Fact]
    public void ReportsAgainAfterTheInterval()
    {
        var progress = new CountingProgress();
        var throttle = new ProgressThrottle(progress, TimeSpan.Zero);

        throttle.Report("path", 1, 1, 1, "first");
        throttle.Report("path", 2, 2, 2, "second");

        Assert.Equal(2, progress.Count);
    }

    [Fact]
    public void AttributeTextIsSharedAndMatchesToString()
    {
        var attributes = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Directory;

        var first = FileAttributesText.Get(attributes);
        var second = FileAttributesText.Get(attributes);

        Assert.Equal(attributes.ToString(), first);
        Assert.Same(first, second);
    }

    private sealed class CountingProgress : IProgress<ScanProgress>
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Report(ScanProgress value)
            => Interlocked.Increment(ref _count);
    }
}
