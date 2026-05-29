using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Custodian.App.Logging;

/// <summary>
/// Application-wide logging composition root. Initialized once in <c>Program.Main</c>.
/// Until DI is introduced (later phase), this is the single ambient factory the App
/// layer pulls loggers from. Safe to call before initialization — returns a no-op logger.
/// </summary>
public static class AppLogging
{
    private static ILoggerFactory? _factory;

    /// <summary>Directory where log files are written.</summary>
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Custodian",
        "logs");

    /// <summary>Builds the factory and its file provider. Call once at startup.</summary>
    public static void Initialize(LogLevel minLevel = LogLevel.Information)
    {
        if (_factory is not null)
        {
            return;
        }

        _factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minLevel);
            builder.AddProvider(new FileLoggerProvider(LogDirectory, minLevel));
        });
    }

    public static ILogger<T> CreateLogger<T>() =>
        _factory?.CreateLogger<T>() ?? NullLogger<T>.Instance;

    public static ILogger CreateLogger(string category) =>
        _factory?.CreateLogger(category) ?? NullLogger.Instance;

    /// <summary>Flushes and disposes the file provider. Call once on shutdown.</summary>
    public static void Shutdown()
    {
        _factory?.Dispose();
        _factory = null;
    }
}
