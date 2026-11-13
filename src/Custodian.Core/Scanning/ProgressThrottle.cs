using System.Diagnostics;
using Custodian.Core.Model;

namespace Custodian.Core.Scanning;

/// <summary>
/// Limits progress reports to one per interval. Safe to call from several threads:
/// when several callers arrive in the same interval, exactly one of them reports.
/// </summary>
internal sealed class ProgressThrottle
{
    private const long NotReported = long.MinValue;

    private readonly IProgress<ScanProgress>? _progress;
    private readonly long _intervalTicks;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private long _lastReportTicks = NotReported;

    public ProgressThrottle(IProgress<ScanProgress>? progress, TimeSpan? interval = null)
    {
        _progress = progress;
        var resolved = interval ?? TimeSpan.FromMilliseconds(250);
        _intervalTicks = (long)(resolved.TotalSeconds * Stopwatch.Frequency);
    }

    public void Report(string currentPath, long filesSeen, long directoriesSeen, long bytesSeen, string message, bool force = false)
    {
        if (_progress is null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp() - _startTimestamp;
        var last = Volatile.Read(ref _lastReportTicks);
        if (!force)
        {
            if (last != NotReported && now - last < _intervalTicks)
            {
                return;
            }

            // Another thread claimed this interval first.
            if (Interlocked.CompareExchange(ref _lastReportTicks, now, last) != last)
            {
                return;
            }
        }
        else
        {
            Volatile.Write(ref _lastReportTicks, now);
        }

        _progress.Report(new ScanProgress(currentPath, filesSeen, directoriesSeen, bytesSeen, message));
    }
}
