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
        await writer.WriteLineAsync("Path,Name,Type,LogicalSizeBytes,AllocatedSizeBytes,FileCount,DirectoryCount,Extension,Attributes,LastWriteUtc,CloudProviderId,CloudProviderName,CloudProviderAccountLabel,CloudProviderRootPath");

        foreach (var entry in result.Root.Flatten())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = result.CloudProvider;
            var line = string.Join(
                ',',
                CsvFieldFormatter.Format(entry.FullPath),
                CsvFieldFormatter.Format(entry.Name),
                entry.IsDirectory ? "Directory" : "File",
                entry.LogicalSizeBytes,
                entry.AllocatedSizeBytes,
                entry.FileCount,
                entry.DirectoryCount,
                CsvFieldFormatter.Format(entry.Extension),
                CsvFieldFormatter.Format(entry.Attributes),
                CsvFieldFormatter.Format(entry.LastWriteTime?.UtcDateTime.ToString("O") ?? string.Empty),
                CsvFieldFormatter.Format(provider?.ProviderId),
                CsvFieldFormatter.Format(provider?.ProviderName),
                CsvFieldFormatter.Format(provider?.AccountLabel),
                CsvFieldFormatter.Format(provider?.RootPath));
            await writer.WriteLineAsync(line);
        }
    }

    public static async Task ExportJsonAsync(ScanResult result, string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

}
