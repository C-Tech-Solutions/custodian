using Custodian.Core.Model;

namespace Custodian.Core.Portable;

public static class PortableCopyPlanner
{
    private static readonly HashSet<char> InvalidFileNameChars = new(
        Enumerable.Range(0, 32)
            .Select(value => (char)value)
            .Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }));
    private static readonly HashSet<string> ReservedWindowsFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

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
            var directoryPaths = new Dictionary<FileSystemEntry, string>();
            ReserveDirectoryDestinations(entry, topFolderName, destinationRoot, usedPaths, directoryPaths);
            AddEmptyDirectories(entry, destinationRoot, items, directoryPaths);
            AddFiles(entry, destinationRoot, items, skipped, usedPaths, directoryPaths);
        }

        return new PortableCopyPlan(items, skipped);
    }

    private static void ReserveDirectoryDestinations(
        FileSystemEntry directory,
        string relativePath,
        string destinationRoot,
        ISet<string> usedPaths,
        Dictionary<FileSystemEntry, string> directoryPaths)
    {
        directoryPaths[directory] = relativePath;
        usedPaths.Add(Path.Combine(destinationRoot, relativePath));

        var childDirectories = directory.Children
            .Where(child => child.IsDirectory)
            .OrderBy(child => child.FullPath, StringComparer.OrdinalIgnoreCase);

        foreach (var child in childDirectories)
        {
            var desiredRelativePath = Path.Combine(relativePath, SanitizeFileName(child.Name));
            var destinationPath = GetAvailableDirectoryPath(Path.Combine(destinationRoot, desiredRelativePath), usedPaths);
            var childRelativePath = Path.GetRelativePath(destinationRoot, destinationPath);
            ReserveDirectoryDestinations(child, childRelativePath, destinationRoot, usedPaths, directoryPaths);
        }
    }

    private static bool AddEmptyDirectories(
        FileSystemEntry directory,
        string destinationRoot,
        List<PortableCopyPlanItem> items,
        IReadOnlyDictionary<FileSystemEntry, string> directoryPaths)
    {
        var containsFile = false;
        foreach (var child in directory.Children)
        {
            if (child.IsDirectory)
            {
                containsFile |= AddEmptyDirectories(child, destinationRoot, items, directoryPaths);
            }
            else
            {
                containsFile = true;
            }
        }

        if (!containsFile)
        {
            var relativePath = directoryPaths[directory];
            var destinationPath = Path.Combine(destinationRoot, relativePath);
            items.Add(new PortableCopyPlanItem(directory, relativePath, destinationPath, IsDirectory: true));
        }

        return containsFile;
    }

    private static void AddFiles(
        FileSystemEntry directory,
        string destinationRoot,
        List<PortableCopyPlanItem> items,
        List<SkippedEntry> skipped,
        ISet<string> usedPaths,
        IReadOnlyDictionary<FileSystemEntry, string> directoryPaths)
    {
        var files = directory.Children
            .Where(child => !child.IsDirectory)
            .OrderBy(child => child.FullPath, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            AddFile(
                file,
                Path.Combine(directoryPaths[directory], SanitizeFileName(file.Name)),
                destinationRoot,
                items,
                skipped,
                usedPaths);
        }

        var childDirectories = directory.Children
            .Where(child => child.IsDirectory)
            .OrderBy(child => child.FullPath, StringComparer.OrdinalIgnoreCase);

        foreach (var child in childDirectories)
        {
            AddFiles(child, destinationRoot, items, skipped, usedPaths, directoryPaths);
        }
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
        if (entries.Count <= 1)
        {
            return entries;
        }

        var selectedDirectoryPaths = entries
            .Where(entry => entry.IsDirectory)
            .Select(entry => NormalizePortablePath(entry.FullPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (selectedDirectoryPaths.Count == 0)
        {
            return entries;
        }

        return entries.Select(entry => new PathEntry(entry, NormalizePortablePath(entry.FullPath)))
            .Where(candidate => !IsCoveredBySelectedDirectory(candidate.Path, selectedDirectoryPaths))
            .Select(candidate => candidate.Entry)
            .ToList();
    }

    private static bool IsCoveredBySelectedDirectory(string candidatePath, ISet<string> selectedDirectoryPaths)
    {
        var slashIndex = candidatePath.LastIndexOf('/');
        while (slashIndex > 0)
        {
            if (selectedDirectoryPaths.Contains(candidatePath[..slashIndex]))
            {
                return true;
            }

            slashIndex = candidatePath.LastIndexOf('/', slashIndex - 1);
        }

        return false;
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
        char[]? chars = null;
        for (var index = 0; index < name.Length; index++)
        {
            if (InvalidFileNameChars.Contains(name[index]))
            {
                chars ??= name.ToCharArray();
                chars[index] = '_';
            }
        }

        if (chars is not null)
        {
            name = new string(chars);
        }

        name = name.TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unnamed";
        }

        return RenameReservedDeviceName(name);
    }

    private static string RenameReservedDeviceName(string name)
    {
        var periodIndex = name.IndexOf('.');
        var baseName = periodIndex < 0
            ? name
            : name[..periodIndex];

        return !string.IsNullOrWhiteSpace(baseName) && ReservedWindowsFileNames.Contains(baseName)
            ? (periodIndex < 0 ? $"{name}_" : $"{baseName}_{name[periodIndex..]}")
            : name;
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

    private static string GetAvailableDirectoryPath(string path, ISet<string> usedPaths)
    {
        if (!usedPaths.Contains(path) && !File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var folderName = Path.GetFileName(path);
        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(directory, $"{folderName} ({index})");
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
