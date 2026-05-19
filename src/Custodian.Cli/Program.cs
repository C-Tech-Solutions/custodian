using Custodian.Core.Export;
using Custodian.Core.Formatting;
using Custodian.Core.Model;
using Custodian.Core.Scanning;
using Custodian.Core.Storage;

var exitCode = await RunAsync(args);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        PrintHelp();
        return 0;
    }

    try
    {
        return args[0].ToLowerInvariant() switch
        {
            "scan" => await ScanAsync(args[1..]),
            "export" => await ExportAsync(args[1..]),
            _ => UsageError($"Unknown command: {args[0]}")
        };
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Scan cancelled.");
        return 130;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static async Task<int> ScanAsync(string[] args)
{
    if (args.Length == 0)
    {
        return UsageError("scan requires a path.");
    }

    var path = args[0];
    var outPath = GetOption(args, "--out");
    var exportPath = GetOption(args, "--export");
    var silent = HasFlag(args, "--silent");
    var allocated = HasFlag(args, "--allocated");
    var mode = ParseMode(GetOption(args, "--mode"));
    var scanner = new DiskScanner();
    var progress = silent
        ? null
        : new Progress<ScanProgress>(p =>
        {
            Console.Write($"\r{p.Message}: {p.FilesSeen:n0} files, {SizeFormatter.Format(p.BytesSeen)}       ");
        });

    var result = await scanner.ScanAsync(new ScanOptions(path, mode, CollectAllocatedSize: allocated), progress);
    if (!silent)
    {
        Console.WriteLine();
        PrintSummary(result);
    }

    if (!string.IsNullOrWhiteSpace(outPath))
    {
        await new ScanStore().SaveAsync(result, outPath);
        if (!silent)
        {
            Console.WriteLine($"Saved {outPath}");
        }
    }

    if (!string.IsNullOrWhiteSpace(exportPath))
    {
        await ExportByExtensionAsync(result, exportPath);
        if (!silent)
        {
            Console.WriteLine($"Exported {exportPath}");
        }
    }

    if (string.IsNullOrWhiteSpace(outPath) && string.IsNullOrWhiteSpace(exportPath) && silent)
    {
        Console.WriteLine($"{result.Root.LogicalSizeBytes}");
    }

    return 0;
}

static async Task<int> ExportAsync(string[] args)
{
    if (args.Length == 0)
    {
        return UsageError("export requires a .custodian-scan file.");
    }

    var scanPath = args[0];
    var format = GetOption(args, "--format") ?? "csv";
    var outPath = GetOption(args, "--out");
    if (string.IsNullOrWhiteSpace(outPath))
    {
        return UsageError("export requires --out <file>.");
    }

    var result = await new ScanStore().LoadAsync(scanPath);
    switch (format.ToLowerInvariant())
    {
        case "csv":
            await ScanExporter.ExportCsvAsync(result, outPath);
            break;
        case "json":
            await ScanExporter.ExportJsonAsync(result, outPath);
            break;
        default:
            return UsageError($"Unsupported export format: {format}");
    }

    Console.WriteLine($"Exported {outPath}");
    return 0;
}

static async Task ExportByExtensionAsync(ScanResult result, string path)
{
    if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    {
        await ScanExporter.ExportJsonAsync(result, path);
    }
    else
    {
        await ScanExporter.ExportCsvAsync(result, path);
    }
}

static ScanMode ParseMode(string? value)
{
    return value?.ToLowerInvariant() switch
    {
        null or "" or "auto" => ScanMode.Auto,
        "recursive" => ScanMode.Recursive,
        "mft" => ScanMode.Mft,
        _ => throw new ArgumentException($"Unsupported scan mode: {value}")
    };
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static bool HasFlag(string[] args, string name)
{
    return args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
}

static int UsageError(string message)
{
    Console.Error.WriteLine(message);
    PrintHelp();
    return 2;
}

static void PrintSummary(ScanResult result)
{
    Console.WriteLine($"Root:       {result.RootPath}");
    Console.WriteLine($"Engine:     {result.Engine}");
    Console.WriteLine($"Size:       {SizeFormatter.Format(result.Root.LogicalSizeBytes)} logical, {SizeFormatter.Format(result.Root.AllocatedSizeBytes)} allocated");
    Console.WriteLine($"Contents:   {result.Root.FileCount:n0} files, {result.Root.DirectoryCount:n0} folders");
    Console.WriteLine($"Duration:   {result.Duration:g}");
    foreach (var timing in result.PhaseTimings)
    {
        Console.WriteLine($"Phase:      {timing.Name}: {timing.Duration.TotalSeconds:0.###}s");
    }

    foreach (var diagnostic in result.Diagnostics)
    {
        Console.WriteLine($"Diagnostic: {diagnostic}");
    }

    if (result.SkippedEntries.Count > 0)
    {
        Console.WriteLine($"Skipped:    {result.SkippedEntries.Count:n0}");
    }
}

static void PrintHelp()
{
    Console.WriteLine("""
        Custodian CLI

        Usage:
          custodian scan <path> [--mode auto|recursive|mft] [--allocated] [--out file.custodian-scan] [--export file.csv|file.json] [--silent]
          custodian export <file.custodian-scan> --format csv|json --out <file>

        Examples:
          custodian scan C:\Data --out data.custodian-scan
          custodian scan \\server\share --export share.csv --silent
          custodian export data.custodian-scan --format json --out data.json
        """);
}
