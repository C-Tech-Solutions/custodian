using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Custodian.App.Logging;

/// <summary>
/// Minimal rolling file logger. Writes one line per entry to a daily file under the
/// configured directory. Designed to be dependency-free (no third-party sink) so it
/// cannot conflict with the pinned Velopack / .NET runtime versions.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 4096);
    private readonly Thread _worker;
    private volatile bool _disposed;

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information)
    {
        _directory = directory;
        _minLevel = minLevel;
        Directory.CreateDirectory(_directory);

        _worker = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "Custodian.FileLogger",
        };
        _worker.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    internal bool IsEnabled(LogLevel level) => !_disposed && level >= _minLevel && level != LogLevel.None;

    internal void Enqueue(string line)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // Non-blocking: if the queue is saturated we drop the line rather than stall the UI.
            _queue.TryAdd(line);
        }
        catch (ObjectDisposedException)
        {
            // The queue was disposed during shutdown.
        }
        catch (InvalidOperationException)
        {
            // The queue was marked complete during shutdown.
        }
    }

    private void ProcessQueue()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    Directory.CreateDirectory(_directory);
                    var path = Path.Combine(_directory, $"custodian-{DateTime.UtcNow:yyyyMMdd}.log");
                    using var writer = new StreamWriter(path, append: true, Encoding.UTF8);
                    writer.Write(line);
                    while (_queue.TryTake(out var extraLine))
                    {
                        writer.Write(extraLine);
                    }
                }
                catch
                {
                    // Logging must never throw into the app. If the disk is full or the
                    // file is locked, the entry is lost — that is acceptable for diagnostics.
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The queue can be disposed during shutdown after the bounded flush wait.
        }
        catch
        {
            // A background logger failure must never crash the application.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();

        try
        {
            _worker.Join(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort flush on shutdown.
        }

        _queue.Dispose();
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var sb = new StringBuilder();
            sb.Append(DateTimeOffset.UtcNow.ToString("O"))
                .Append(" [").Append(LevelLabel(logLevel)).Append("] ")
                .Append(category).Append(" - ").Append(message);

            if (exception is not null)
            {
                sb.AppendLine().Append(exception);
            }

            sb.AppendLine();
            provider.Enqueue(sb.ToString());
        }

        private static string LevelLabel(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
