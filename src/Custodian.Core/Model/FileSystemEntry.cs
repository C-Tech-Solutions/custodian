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
    public string PortableObjectId { get; set; } = string.Empty;
    public string PortablePersistentId { get; set; } = string.Empty;
    public List<FileSystemEntry> Children { get; } = [];

    /// <summary>
    /// Returns this entry and every descendant in pre-order: an entry, then each of its
    /// children's subtrees in <see cref="Children"/> order.
    /// </summary>
    public IEnumerable<FileSystemEntry> Flatten()
    {
        var pending = new Stack<FileSystemEntry>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            var entry = pending.Pop();
            yield return entry;

            var children = entry.Children;
            for (var i = children.Count - 1; i >= 0; i--)
            {
                pending.Push(children[i]);
            }
        }
    }
}
