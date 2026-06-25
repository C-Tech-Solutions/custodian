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
}
