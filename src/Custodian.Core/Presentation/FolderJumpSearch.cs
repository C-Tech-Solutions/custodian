namespace Custodian.Core.Presentation;

/// <summary>
/// The jump box's current folder index and its newest search. Searches that wait on an index
/// still being built resume together and can finish in any order, so only the newest search
/// against the current index returns rows. Use from one thread, such as the UI thread.
/// </summary>
public sealed class FolderJumpSearch
{
    private int _version;

    /// <summary>The index searches run against. Replacing it supersedes searches still waiting.</summary>
    public BackgroundFolderJumpIndex Index { get; set; } = BackgroundFolderJumpIndex.Empty;

    /// <summary>
    /// Searches <see cref="Index"/>. Returns null when a newer search started, or the index was
    /// replaced, before this search finished.
    /// </summary>
    public async Task<IReadOnlyList<FolderJumpRow>?> SearchAsync(string query, int take = 50)
    {
        var index = Index;
        var version = ++_version;
        // Resumes on the caller's context, so the checks below run on the thread that searches.
        var rows = await index.SearchAsync(query, take).ConfigureAwait(true);
        return version == _version && ReferenceEquals(index, Index) ? rows : null;
    }
}
