namespace Custodian.Core.Scanning;

public static class ScanPathUtility
{
    public static string NormalizeRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A scan path is required.", nameof(rootPath));
        }

        var trimmed = rootPath.Trim();
        if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
        {
            return char.ToUpperInvariant(trimmed[0]) + @":\";
        }

        var fullPath = Path.GetFullPath(trimmed);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(
                TrimForComparison(fullPath),
                TrimForComparison(root),
                StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsVolumeRoot(string rootPath)
    {
        var normalized = NormalizeRoot(rootPath);
        var root = Path.GetPathRoot(normalized);
        return !string.IsNullOrWhiteSpace(root) &&
               string.Equals(
                   TrimForComparison(normalized),
                   TrimForComparison(root),
                   StringComparison.OrdinalIgnoreCase);
    }

    public static string TrimForComparison(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
