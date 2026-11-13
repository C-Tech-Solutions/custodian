namespace Custodian.Tui;

internal sealed record TuiLaunchOptions(
    string? TargetPath,
    string? ScanFilePath,
    bool AutoScan,
    string Mode,
    bool CollectAllocatedSize)
{
    public static TuiLaunchOptions Parse(IReadOnlyList<string> args, string? elevatedLaunchPath = null)
    {
        string? targetPath = elevatedLaunchPath;
        string? scanFilePath = null;
        var autoScan = false;
        var mode = "auto";
        var allocated = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--custodian-path", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            if (string.Equals(arg, "--scan", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                targetPath = args[++i];
                autoScan = true;
                continue;
            }

            if (string.Equals(arg, "--open", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                scanFilePath = args[++i];
                autoScan = false;
                continue;
            }

            if (string.Equals(arg, "--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                mode = args[++i];
                continue;
            }

            if (string.Equals(arg, "--allocated", StringComparison.OrdinalIgnoreCase))
            {
                allocated = true;
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            if (arg.EndsWith(".custodian-scan", StringComparison.OrdinalIgnoreCase))
            {
                scanFilePath = arg;
            }
            else
            {
                targetPath = arg;
            }
        }

        return new TuiLaunchOptions(targetPath, scanFilePath, autoScan, mode, allocated);
    }

    /// <summary>
    /// Returns <paramref name="args"/> with relative scan and scan-file paths made absolute.
    /// The startup elevation relaunch runs in a trusted working directory, not the caller's,
    /// so relative paths have to be resolved before it. Windows does the resolving, so a
    /// drive-relative path such as <c>D:Scans</c> uses the caller's current directory on D:.
    /// </summary>
    public static IReadOnlyList<string> ResolveRelativePaths(IReadOnlyList<string> args)
        => ResolveRelativePaths(args, Path.GetFullPath);

    internal static IReadOnlyList<string> ResolveRelativePaths(IReadOnlyList<string> args, Func<string, string> getFullPath)
    {
        var resolved = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            resolved.Add(arg);

            if (string.Equals(arg, "--custodian-path", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--mode", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    resolved.Add(args[++i]);
                }

                continue;
            }

            if (string.Equals(arg, "--scan", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--open", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    resolved.Add(ResolveRelativePath(args[++i], getFullPath));
                }

                continue;
            }

            if (!arg.StartsWith("-", StringComparison.Ordinal))
            {
                resolved[^1] = ResolveRelativePath(arg, getFullPath);
            }
        }

        return resolved;
    }

    // Fully qualified paths stay as typed, and so does a bare drive such as "C:", which
    // ScanPathUtility.NormalizeRoot treats as that drive's root. Root-relative (\Scans)
    // and drive-relative (C:Scans) forms still depend on the caller's directory, so they
    // are resolved along with plain relative paths.
    private static string ResolveRelativePath(string path, Func<string, string> getFullPath)
        => string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path) || IsBareDrive(path)
            ? path
            : getFullPath(path);

    private static bool IsBareDrive(string path)
        => path.Length == 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
}
