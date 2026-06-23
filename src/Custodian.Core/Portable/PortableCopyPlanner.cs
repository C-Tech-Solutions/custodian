using Custodian.Core.Model;

namespace Custodian.Core.Portable;

public static class PortableCopyPlanner
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    public static PortableCopyPlan BuildPlan(
        IEnumerable<FileSystemEntry> selectedEntries,
        string destinationRoot,
        ISet<string>? existingPaths = null)
    {
        ArgumentNullException.ThrowIfNull(selectedEntries);

        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            throw new ArgumentException("Destination folder is required.", nameof(destinationRoot));
        }

        var selected = DistinctEntries(selectedEntries).ToList();
        var normalizedSelection = RemoveSelectionsCoveredBySelectedFolders(selected);
        var skipped = new List<SkippedEntry>();
        var usedPaths = existingPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedTopLevelDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<PortableCopyPlanItem>();

        foreach (var entry in normalizedSelection)
        {
            if (!entry.IsDirectory)
            {
                AddFile(entry, SanitizeFileName(entry.Name), destinationRoot, items, skipped, usedPaths);
                continue;
            }

            var topFolderName = GetAvailableTopLevelDirectorySegment(
                SanitizeFileName(entry.Name),
                destinationRoot,
                usedTopLevelDirectories,
                usedPaths);
            usedPaths.Add(Path.Combine(destinationRoot, topFolderName));
            AddEmptyDirectories(entry, entry, topFolderName, destinationRoot, items, usedPaths);

            var files = entry.Flatten()
                .Where(child => !child.IsDirectory)
                .OrderBy(child => child.FullPath, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var relativeInsideFolder = RelativePortablePath(entry, file);
                var relativePath = Path.Combine(topFolderName, relativeInsideFolder);
                AddFile(file, relativePath, destinationRoot, items, skipped, usedPaths);
            }
        }

        return new PortableCopyPlan(items, skipped);
    }

    private static bool AddEmptyDirectories(
        FileSystemEntry root,
        FileSystemEntry directory,
        string topFolderName,
        string destinationRoot,
        List<PortableCopyPlanItem> items,
        ISet<string> usedPaths)
    {
        var containsFile = false;
        foreach (var child in directory.Children)
        {
            if (child.IsDirectory)
            {
                containsFile |= AddEmptyDirectories(root, child, topFolderName, destinationRoot, items, usedPaths);
            }
            else
            {
                containsFile = true;
            }
        }

        if (!containsFile)
        {
            var relativeInsideFolder = ReferenceEquals(root, directory)
                ? string.Empty
                : RelativePortablePath(root, directory);
            var relativePath = string.IsNullOrWhiteSpace(relativeInsideFolder)
                ? topFolderName
                : Path.Combine(topFolderName, relativeInsideFolder);
            var destinationPath = Path.Combine(destinationRoot, SanitizeRelativePath(relativePath));
            usedPaths.Add(destinationPath);
            items.Add(new PortableCopyPlanItem(directory, relativePath, destinationPath, IsDirectory: true));
        }

        return containsFile;
    }

    private static void AddFile(
        FileSystemEntry entry,
        string relativePath,
        string destinationRoot,
        List<PortableCopyPlanItem> items,
        List<SkippedEntry> skipped,
        ISet<string> usedPaths)
    {
        if (string.IsNullOrWhiteSpace(entry.PortableObjectId) &&
            string.IsNullOrWhiteSpace(entry.PortablePersistentId))
        {
            skipped.Add(new SkippedEntry(entry.FullPath, "Portable object identity is missing. Rescan this phone to copy this item."));
            return;
        }

        var destinationPath = GetAvailablePath(Path.Combine(destinationRoot, SanitizeRelativePath(relativePath)), usedPaths);
        usedPaths.Add(destinationPath);
        items.Add(new PortableCopyPlanItem(entry, relativePath, destinationPath));
    }

    private static IEnumerable<FileSystemEntry> DistinctEntries(IEnumerable<FileSystemEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var key = EntryKey(entry);
            if (seen.Add(key))
            {
                yield return entry;
            }
        }
    }

    private static IReadOnlyList<FileSystemEntry> RemoveSelectionsCoveredBySelectedFolders(IReadOnlyList<FileSystemEntry> entries)
    {
        var selectedDirectories = entries
            .Where(entry => entry.IsDirectory)
            .Select(entry => new PathEntry(entry, NormalizePortablePath(entry.FullPath)))
            .ToList();

        if (selectedDirectories.Count == 0)
        {
            return entries;
        }

        return entries.Select(entry => new PathEntry(entry, NormalizePortablePath(entry.FullPath)))
            .Where(candidate => !selectedDirectories.Any(directory =>
                !ReferenceEquals(directory.Entry, candidate.Entry) &&
                IsPortableDescendantOf(candidate.Path, directory.Path)))
            .Select(candidate => candidate.Entry)
            .ToList();
    }

    private static bool IsPortableDescendantOf(string candidatePath, string ancestorPath)
    {
        return candidatePath.Length > ancestorPath.Length &&
            candidatePath.StartsWith(ancestorPath, StringComparison.OrdinalIgnoreCase) &&
            candidatePath[ancestorPath.Length] == '/';
    }

    private static string RelativePortablePath(FileSystemEntry root, FileSystemEntry file)
    {
        var rootPath = root.FullPath.TrimEnd('/', '\\');
        var fullPath = file.FullPath;
        if (fullPath.Length > rootPath.Length &&
            fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) &&
            (fullPath[rootPath.Length] == '/' || fullPath[rootPath.Length] == '\\'))
        {
            return SanitizeRelativePath(fullPath[(rootPath.Length + 1)..]);
        }

        return SanitizeFileName(file.Name);
    }

    private static string SanitizeRelativePath(string relativePath)
    {
        var segments = relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeFileName)
            .Where(segment => !string.IsNullOrWhiteSpace(segment));

        var sanitized = Path.Combine(segments.ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Unnamed" : sanitized;
    }

    public static string SanitizeFileName(string value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "Unnamed" : value.Trim();
        foreach (var invalid in InvalidFileNameChars)
        {
            name = name.Replace(invalid, '_');
        }

        name = name.TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(name) ? "Unnamed" : name;
    }

    private static string GetAvailableTopLevelDirectorySegment(
        string segment,
        string destinationRoot,
        ISet<string> usedSegments,
        ISet<string> usedPaths)
    {
        if (IsAvailableTopLevelDirectorySegment(segment, destinationRoot, usedSegments, usedPaths))
        {
            usedSegments.Add(segment);
            return segment;
        }

        for (var index = 1; ; index++)
        {
            var candidate = $"{segment} ({index})";
            if (IsAvailableTopLevelDirectorySegment(candidate, destinationRoot, usedSegments, usedPaths))
            {
                usedSegments.Add(candidate);
                return candidate;
            }
        }
    }

    private static bool IsAvailableTopLevelDirectorySegment(
        string segment,
        string destinationRoot,
        ISet<string> usedSegments,
        ISet<string> usedPaths)
    {
        var destinationPath = Path.Combine(destinationRoot, segment);
        return !usedSegments.Contains(segment) &&
            !usedPaths.Contains(destinationPath) &&
            !File.Exists(destinationPath) &&
            !Directory.Exists(destinationPath);
    }

    private static string GetAvailablePath(string path, ISet<string> usedPaths)
    {
        if (!usedPaths.Contains(path) && !File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(directory, $"{fileName} ({index}){extension}");
            if (!usedPaths.Contains(candidate) && !File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string EntryKey(FileSystemEntry entry)
        => !string.IsNullOrWhiteSpace(entry.PortableObjectId)
            ? entry.PortableObjectId
            : entry.FullPath;

    private static string NormalizePortablePath(string path)
        => string.Join('/', path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private sealed record PathEntry(FileSystemEntry Entry, string Path);
}
