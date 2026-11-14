using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

/// <summary>
/// The folder jump index for one scan, built on a thread-pool thread so that showing scan
/// results does not wait for it. Searches wait for the build to finish.
/// </summary>
public sealed class BackgroundFolderJumpIndex
{
    private BackgroundFolderJumpIndex(Task<IReadOnlyList<FolderJumpRow>> rows)
    {
        Rows = rows;
    }

    /// <summary>An index with no folders, for when no scan is shown.</summary>
    public static BackgroundFolderJumpIndex Empty { get; } = new(Task.FromResult<IReadOnlyList<FolderJumpRow>>([]));

    /// <summary>
    /// Every folder in the scan, ordered as <see cref="ScanViewProjector.FolderJumpIndex"/> orders them.
    /// </summary>
    public Task<IReadOnlyList<FolderJumpRow>> Rows { get; }

    /// <summary>True once <see cref="Rows"/> has finished, successfully or not.</summary>
    public bool IsBuilt => Rows.IsCompleted;

    /// <summary>Starts building the index for <paramref name="root"/> and returns at once.</summary>
    public static BackgroundFolderJumpIndex Start(FileSystemEntry root)
    {
        return new BackgroundFolderJumpIndex(Task.Run(() => ScanViewProjector.FolderJumpIndex(root)));
    }

    /// <summary>An index whose build is <paramref name="rows"/>; lets tests decide when it finishes.</summary>
    internal static BackgroundFolderJumpIndex FromTask(Task<IReadOnlyList<FolderJumpRow>> rows)
    {
        return new BackgroundFolderJumpIndex(rows);
    }

    /// <summary>
    /// Waits for the index, then returns up to <paramref name="take"/> folders whose name or
    /// path contains <paramref name="query"/>. Completes synchronously once the index is built.
    /// </summary>
    public async Task<IReadOnlyList<FolderJumpRow>> SearchAsync(string query, int take = 50)
    {
        var rows = await Rows.ConfigureAwait(false);
        return ScanViewProjector.FolderJumpRows(rows, query, take);
    }
}
