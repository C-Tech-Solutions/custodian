using System.Diagnostics;
using Custodian.Core.Model;

namespace Custodian.Core.Scanning;

internal sealed class ProgressThrottle
{
    private readonly IProgress<ScanProgress>? _progress;
    private readonly TimeSpan _interval;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private TimeSpan _lastReport = TimeSpan.MinValue;

    public ProgressThrottle(IProgress<ScanProgress>? progress, TimeSpan? interval = null)
    {
        _progress = progress;
        _interval = interval ?? TimeSpan.FromMilliseconds(250);
    }

    public void Report(string currentPath, long filesSeen, long directoriesSeen, long bytesSeen, string message, bool force = false)
    {
        if (_progress is null)
        {
            return;
        }

        var elapsed = _watch.Elapsed;
        if (!force && _lastReport != TimeSpan.MinValue && elapsed - _lastReport < _interval)
        {
            return;
        }

        _lastReport = elapsed;
        _progress.Report(new ScanProgress(currentPath, filesSeen, directoriesSeen, bytesSeen, message));
    }
}
