using System.Text.Json.Serialization;

namespace Custodian.Core.Model;

public sealed class ScanResult
{
    public string RootPath { get; set; } = string.Empty;
    public string Engine { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public FileSystemEntry Root { get; set; } = new();
    public List<SkippedEntry> SkippedEntries { get; set; } = [];
    public List<ScanPhaseTiming> PhaseTimings { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
    [JsonIgnore]
    public ScanGlobalIndex? GlobalIndex { get; set; }
    public TimeSpan Duration => CompletedAt - StartedAt;
}
