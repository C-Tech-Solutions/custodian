namespace Custodian.Core.Model;

public sealed class FileSystemEntry
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long LogicalSizeBytes { get; set; }
    public long AllocatedSizeBytes { get; set; }
    public long FileCount { get; set; }
    public long DirectoryCount { get; set; }
    public string Extension { get; set; } = string.Empty;
    public string Attributes { get; set; } = string.Empty;
    public DateTimeOffset? LastWriteTime { get; set; }
    public List<FileSystemEntry> Children { get; } = [];

    public IEnumerable<FileSystemEntry> Flatten()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var nested in child.Flatten())
            {
                yield return nested;
            }
        }
    }
}
