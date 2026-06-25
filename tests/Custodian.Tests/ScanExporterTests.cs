using System.Text;
using System.Text.Json;
using Custodian.Core.Export;
using Custodian.Core.Model;

namespace Custodian.Tests;

public sealed class ScanExporterTests
{
    [Fact]
    public async Task CsvWritesHeaderAndOneRowPerEntry()
    {
        var result = SampleResult();
        using var temp = new TempFile(".csv");

        await ScanExporter.ExportCsvAsync(result, temp.Path);
        var lines = await ReadAllLinesAsync(temp.Path);

        Assert.Equal(
            "Path,Name,Type,LogicalSizeBytes,AllocatedSizeBytes,FileCount,DirectoryCount,Extension,Attributes,LastWriteUtc,CloudProviderId,CloudProviderName,CloudProviderAccountLabel,CloudProviderRootPath",
            lines[0]);

        var entryCount = result.Root.Flatten().Count();
        Assert.Equal(entryCount, lines.Count - 1);
    }

    [Fact]
    public async Task CsvQuotesFieldsContainingCommasQuotesAndNewlines()
    {
        var root = MakeDirectory(@"C:\", 0);
        root.Children.Add(new FileSystemEntry
        {
            Name = "weird, \"name\"\nwith newline",
            FullPath = "C:\\weird, \"name\"\nwith newline",
            IsDirectory = false,
            Extension = ".bin",
            FileCount = 1
        });
        var result = Result(root);
        using var temp = new TempFile(".csv");

        await ScanExporter.ExportCsvAsync(result, temp.Path);
        var text = await ReadAllTextAsync(temp.Path);

        // Comma + embedded quotes => wrapped in quotes with quotes doubled.
        Assert.Contains("\"weird, \"\"name\"\"", text);
        // A field with no special characters is left unquoted.
        Assert.Contains(",.bin,", text);
    }

    [Theory]
    [InlineData("=cmd|'/c calc'!A1")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(1)")]
    [InlineData("\tleading-tab")]
    public async Task CsvNeutralizesFormulaTriggersInAttackerControlledNames(string maliciousName)
    {
        var root = MakeDirectory(@"C:\", 0);
        root.Children.Add(new FileSystemEntry
        {
            Name = maliciousName,
            FullPath = "C:\\" + maliciousName,
            IsDirectory = false,
            Extension = ".txt",
            FileCount = 1
        });
        var result = Result(root);
        using var temp = new TempFile(".csv");

        await ScanExporter.ExportCsvAsync(result, temp.Path);
        var text = await ReadAllTextAsync(temp.Path);

        // The dangerous leading character must be prefixed with ' so spreadsheets treat
        // it as literal text rather than a formula. The raw value must not appear at the
        // start of any field (i.e. right after a comma or a wrapping quote).
        Assert.DoesNotContain("," + maliciousName, text);
        Assert.Contains("'" + maliciousName[0], text);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("=SUM(1)", "'=SUM(1)")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\tleading-tab", "'\tleading-tab")]
    public void CsvFieldFormatterFormatsSharedExportFields(string? value, string expected)
    {
        Assert.Equal(expected, CsvFieldFormatter.Format(value));
    }

    [Fact]
    public async Task CsvWritesUtf8Bom()
    {
        var result = SampleResult();
        using var temp = new TempFile(".csv");

        await ScanExporter.ExportCsvAsync(result, temp.Path);
        var bytes = await System.IO.File.ReadAllBytesAsync(temp.Path);

        Assert.True(bytes.Length >= 3);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
    }

    [Fact]
    public async Task CsvFormatsEmptyLastWriteTimeAsBlank()
    {
        var root = MakeDirectory(@"C:\", 0);
        root.Children.Add(new FileSystemEntry
        {
            Name = "no-timestamp.txt",
            FullPath = @"C:\no-timestamp.txt",
            IsDirectory = false,
            Extension = ".txt",
            FileCount = 1,
            LastWriteTime = null
        });
        var result = Result(root);
        using var temp = new TempFile(".csv");

        await ScanExporter.ExportCsvAsync(result, temp.Path);
        var lines = await ReadAllLinesAsync(temp.Path);

        var row = lines.Single(l => l.StartsWith(@"C:\no-timestamp.txt", StringComparison.Ordinal));
        Assert.Contains(",,", row); // empty LastWriteUtc followed by empty provider fields
    }

    [Fact]
    public async Task CsvWritesCloudProviderMetadataOnRows()
    {
        var result = SampleResult();
        result.CloudProvider = new CloudProviderMetadata(
            "onedrive",
            "OneDrive",
            "Personal",
            @"C:\Users\Me\OneDrive");
        using var temp = new TempFile(".csv");

        await ScanExporter.ExportCsvAsync(result, temp.Path);
        var lines = await ReadAllLinesAsync(temp.Path);

        Assert.EndsWith(@",onedrive,OneDrive,Personal,C:\Users\Me\OneDrive", lines[1]);
    }

    [Fact]
    public async Task JsonWritesMetadataAndFullTree()
    {
        // JSON is an export-only format (the app loads via ScanStore/SQLite, not JSON),
        // so this asserts what is written, not a lossless deserialize round-trip — note
        // FileSystemEntry.Children is get-only and would not rehydrate via System.Text.Json.
        var result = SampleResult();
        using var temp = new TempFile(".json");

        await ScanExporter.ExportJsonAsync(result, temp.Path);
        using var document = JsonDocument.Parse(await System.IO.File.ReadAllBytesAsync(temp.Path));
        var root = document.RootElement;

        Assert.Equal(result.RootPath, root.GetProperty("RootPath").GetString());
        Assert.Equal(result.Engine, root.GetProperty("Engine").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("CloudProvider").ValueKind);

        // The whole tree is serialized, including nested children and their fields.
        var writtenPaths = new List<string>();
        CollectPaths(root.GetProperty("Root"), writtenPaths);
        var expectedPaths = result.Root.Flatten().Select(e => e.FullPath).OrderBy(p => p).ToList();
        Assert.Equal(expectedPaths, writtenPaths.OrderBy(p => p).ToList());

        static void CollectPaths(JsonElement entry, List<string> into)
        {
            into.Add(entry.GetProperty("FullPath").GetString()!);
            foreach (var child in entry.GetProperty("Children").EnumerateArray())
            {
                CollectPaths(child, into);
            }
        }
    }

    [Fact]
    public async Task JsonWritesCloudProviderMetadata()
    {
        var result = SampleResult();
        result.CloudProvider = new CloudProviderMetadata(
            "onedrive",
            "OneDrive",
            "Personal",
            @"C:\Users\Me\OneDrive");
        using var temp = new TempFile(".json");

        await ScanExporter.ExportJsonAsync(result, temp.Path);
        using var document = JsonDocument.Parse(await System.IO.File.ReadAllBytesAsync(temp.Path));

        var provider = document.RootElement.GetProperty("CloudProvider");
        Assert.Equal("onedrive", provider.GetProperty("ProviderId").GetString());
        Assert.Equal("OneDrive", provider.GetProperty("ProviderName").GetString());
        Assert.Equal("Personal", provider.GetProperty("AccountLabel").GetString());
        Assert.Equal(@"C:\Users\Me\OneDrive", provider.GetProperty("RootPath").GetString());
    }

    private static async Task<string> ReadAllTextAsync(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task<List<string>> ReadAllLinesAsync(string path)
    {
        var lines = new List<string>();
        using var reader = new StreamReader(path, Encoding.UTF8);
        while (await reader.ReadLineAsync() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static ScanResult SampleResult()
    {
        var root = MakeDirectory(@"C:\", 140);
        var alpha = MakeDirectory(@"C:\Alpha", 140);
        alpha.Children.Add(MakeFile(@"C:\Alpha\a.bin", 80, ".bin"));
        alpha.Children.Add(MakeFile(@"C:\Alpha\b.log", 60, ".log"));
        root.Children.Add(alpha);
        return Result(root);
    }

    private static ScanResult Result(FileSystemEntry root) => new()
    {
        RootPath = root.FullPath,
        Root = root,
        Engine = "Test",
        StartedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        CompletedAt = new DateTimeOffset(2026, 1, 2, 3, 5, 6, TimeSpan.Zero)
    };

    private static FileSystemEntry MakeDirectory(string path, long size) => new()
    {
        Name = Path.GetFileName(path.TrimEnd('\\')),
        FullPath = path,
        IsDirectory = true,
        LogicalSizeBytes = size,
        AllocatedSizeBytes = size
    };

    private static FileSystemEntry MakeFile(string path, long size, string extension) => new()
    {
        Name = Path.GetFileName(path),
        FullPath = path,
        IsDirectory = false,
        LogicalSizeBytes = size,
        AllocatedSizeBytes = size,
        FileCount = 1,
        Extension = extension,
        LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    };

    private sealed class TempFile(string extension) : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"custodian-test-{Guid.NewGuid():N}{extension}");

        public void Dispose()
        {
            try
            {
                if (System.IO.File.Exists(Path))
                {
                    System.IO.File.Delete(Path);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
