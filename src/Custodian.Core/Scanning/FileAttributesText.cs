using System.Collections.Concurrent;

namespace Custodian.Core.Scanning;

/// <summary>
/// Shares one string per distinct <see cref="FileAttributes"/> value across a scan.
/// A whole drive has only a few dozen distinct values, so caching
/// <see cref="Enum.ToString()"/> avoids one string allocation per entry.
/// Safe to call from several threads.
/// </summary>
internal static class FileAttributesText
{
    // Far above the few dozen values a real drive has; guards against unbounded growth.
    private const int MaxCachedValues = 4096;

    private static readonly ConcurrentDictionary<FileAttributes, string> Cache = new();

    public static string Get(FileAttributes attributes)
    {
        if (Cache.TryGetValue(attributes, out var text))
        {
            return text;
        }

        text = attributes.ToString();
        return Cache.Count < MaxCachedValues
            ? Cache.GetOrAdd(attributes, text)
            : text;
    }
}
