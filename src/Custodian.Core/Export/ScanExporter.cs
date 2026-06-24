using System.Text;
using System.Text.Json;
using Custodian.Core.Model;

namespace Custodian.Core.Export;

public static class ScanExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task ExportCsvAsync(ScanResult result, string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("Path,Name,Type,LogicalSizeBytes,AllocatedSizeBytes,FileCount,DirectoryCount,Extension,Attributes,LastWriteUtc");

        foreach (var entry in result.Root.Flatten())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = string.Join(
                ',',
                Csv(entry.FullPath),
                Csv(entry.Name),
                entry.IsDirectory ? "Directory" : "File",
                entry.LogicalSizeBytes,
                entry.AllocatedSizeBytes,
                entry.FileCount,
                entry.DirectoryCount,
                Csv(entry.Extension),
                Csv(entry.Attributes),
                Csv(entry.LastWriteTime?.UtcDateTime.ToString("O") ?? string.Empty));
            await writer.WriteLineAsync(line);
        }
    }

    public static async Task ExportJsonAsync(ScanResult result, string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static string Csv(string value)
    {
        var sanitized = NeutralizeFormula(value);
        if (sanitized.Contains('"') || sanitized.Contains(',') || sanitized.Contains('\n') || sanitized.Contains('\r'))
        {
            return "\"" + sanitized.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return sanitized;
    }

    // File names, paths, and attributes come from the scanned volume and are fully
    // attacker-controllable. A value that begins with a spreadsheet formula trigger
    // (=, +, -, @, tab, CR) is treated as a live formula by Excel/LibreOffice when the
    // CSV is opened, enabling DDE/command execution. Prefix such values with a single
    // quote so they are imported as literal text.
    private static string NeutralizeFormula(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + value
            : value;
    }
}
