using Custodian.Core.Model;
using Custodian.Core.Storage;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Text;

namespace Custodian.Tests;

public sealed class ScanStoreValidationTests
{
    [Theory]
    [InlineData("UPDATE metadata SET value = 'custodian-scan-v999' WHERE key = 'format'")]
    [InlineData("UPDATE entries SET logical_size_bytes = -1 WHERE id = 2")]
    [InlineData("UPDATE entries SET file_count = -1 WHERE id = 1")]
    [InlineData("UPDATE entries SET parent_id = 999 WHERE id = 2")]
    [InlineData("UPDATE entries SET parent_id = id WHERE id = 2")]
    [InlineData("UPDATE entries SET is_directory = 2 WHERE id = 1")]
    [InlineData("UPDATE entries SET parent_id = CASE id WHEN 2 THEN 3 ELSE 2 END WHERE id IN (2, 3)")]
    public async Task LoadRejectsPreviouslyAcceptedInvalidScans(string mutation)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, mutation);

        await Assert.ThrowsAsync<InvalidDataException>(() => new ScanStore().LoadAsync(temp.Path));
    }

    [Theory]
    [InlineData("format")]
    [InlineData("root_path")]
    [InlineData("engine")]
    [InlineData("started_at")]
    [InlineData("completed_at")]
    public async Task LoadRejectsMissingRequiredMetadata(string key)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, $"DELETE FROM metadata WHERE key = '{key}'");

        await AssertInvalidAsync(temp.Path);
    }

    [Theory]
    [InlineData("format", "")]
    [InlineData("format", "some-other-file")]
    [InlineData("root_path", "   ")]
    [InlineData("engine", "")]
    [InlineData("started_at", "not-a-timestamp")]
    [InlineData("started_at", "12:34")]
    [InlineData("completed_at", "2026-01-03")]
    [InlineData("completed_at", "2026-01-01T00:00:00.0000000Z")]
    [InlineData("source_kind", "not-a-source")]
    [InlineData("source_kind", "999")]
    [InlineData("source_kind", "")]
    public async Task LoadRejectsInvalidMetadataValues(string key, string value)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, "INSERT OR REPLACE INTO metadata VALUES ($key, $value)", ("$key", key), ("$value", value));

        await AssertInvalidAsync(temp.Path);
    }

    [Theory]
    [InlineData("INSERT INTO metadata VALUES ('ROOT_PATH', 'C:\\other')")]
    [InlineData("UPDATE metadata SET value = X'1234' WHERE key = 'engine'")]
    [InlineData("UPDATE metadata SET key = X'1234' WHERE key = 'engine'")]
    [InlineData("UPDATE metadata SET value = NULL WHERE key = 'engine'")]
    [InlineData("UPDATE metadata SET key = NULL WHERE key = 'engine'")]
    [InlineData("INSERT INTO metadata SELECT * FROM metadata WHERE key = 'engine'")]
    public async Task LoadRejectsAmbiguousOrWronglyTypedMetadata(string mutation)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await RemoveConstraintsAsync(temp.Path, "metadata");
        await ExecuteAsync(temp.Path, mutation);

        await AssertInvalidAsync(temp.Path);
    }

    [Fact]
    public async Task LoadValidatesValuesWithoutRequiringSchemaConstraints()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        foreach (var table in new[] { "metadata", "entries", "skipped_entries" })
        {
            await RemoveConstraintsAsync(temp.Path, table);
        }

        var loaded = await new ScanStore().LoadAsync(temp.Path);

        Assert.Equal(3, loaded.Root.Flatten().Count());
        Assert.Single(loaded.SkippedEntries);
    }

    public static TheoryData<string, string> RequiredColumns => new()
    {
        { "metadata", "key" }, { "metadata", "value" },
        { "entries", "id" }, { "entries", "parent_id" }, { "entries", "name" },
        { "entries", "full_path" }, { "entries", "is_directory" },
        { "entries", "logical_size_bytes" }, { "entries", "allocated_size_bytes" },
        { "entries", "file_count" }, { "entries", "directory_count" },
        { "entries", "extension" }, { "entries", "attributes" }, { "entries", "last_write_utc" },
        { "skipped_entries", "id" }, { "skipped_entries", "path" }, { "skipped_entries", "reason" }
    };

    [Theory]
    [MemberData(nameof(RequiredColumns))]
    public async Task LoadRejectsMissingSchemaColumns(string table, string column)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, $"ALTER TABLE {table} RENAME COLUMN {column} TO unexpected_column");

        await AssertInvalidAsync(temp.Path);
    }

    [Theory]
    [InlineData("metadata", false)]
    [InlineData("entries", false)]
    [InlineData("skipped_entries", false)]
    [InlineData("metadata", true)]
    [InlineData("entries", true)]
    [InlineData("skipped_entries", true)]
    public async Task LoadRejectsMissingTablesAndViews(string table, bool replaceWithView)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, $"ALTER TABLE {table} RENAME TO hidden_{table}");
        if (replaceWithView)
        {
            await ExecuteAsync(temp.Path, $"CREATE VIEW {table} AS SELECT * FROM hidden_{table}");
        }

        await AssertInvalidAsync(temp.Path);
    }

    [Fact]
    public async Task LoadRejectsGeneratedColumns()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, "ALTER TABLE entries ADD COLUMN derived TEXT GENERATED ALWAYS AS (name || name)");

        await AssertInvalidAsync(temp.Path);
    }

    [Theory]
    [InlineData("metadata", "key", "INTEGER")]
    [InlineData("metadata", "value", "BLOB")]
    [InlineData("entries", "file_count", "TEXT")]
    [InlineData("entries", "name", "BLOB")]
    [InlineData("skipped_entries", "path", "INTEGER")]
    public async Task LoadRejectsIncompatibleDeclaredColumnTypes(string table, string column, string declaredType)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, $"ALTER TABLE {table} RENAME COLUMN {column} TO old_column; ALTER TABLE {table} ADD COLUMN {column} {declaredType}");

        await AssertInvalidAsync(temp.Path);
    }

    [Theory]
    [InlineData("id", "NULL")]
    [InlineData("id", "2.5")]
    [InlineData("id", "'bad'")]
    [InlineData("id", "0")]
    [InlineData("id", "-1")]
    [InlineData("parent_id", "1.5")]
    [InlineData("parent_id", "'bad'")]
    [InlineData("parent_id", "0")]
    [InlineData("parent_id", "-1")]
    [InlineData("name", "NULL")]
    [InlineData("name", "X'1234'")]
    [InlineData("full_path", "NULL")]
    [InlineData("full_path", "X'1234'")]
    [InlineData("full_path", "''")]
    [InlineData("is_directory", "NULL")]
    [InlineData("is_directory", "0.5")]
    [InlineData("logical_size_bytes", "NULL")]
    [InlineData("logical_size_bytes", "10.5")]
    [InlineData("logical_size_bytes", "'bad'")]
    [InlineData("allocated_size_bytes", "NULL")]
    [InlineData("allocated_size_bytes", "-1")]
    [InlineData("file_count", "NULL")]
    [InlineData("file_count", "1.5")]
    [InlineData("directory_count", "NULL")]
    [InlineData("directory_count", "-1")]
    [InlineData("extension", "NULL")]
    [InlineData("extension", "X'1234'")]
    [InlineData("attributes", "NULL")]
    [InlineData("attributes", "X'1234'")]
    [InlineData("last_write_utc", "X'1234'")]
    [InlineData("last_write_utc", "'not-a-date'")]
    [InlineData("last_write_utc", "'12:34'")]
    [InlineData("last_write_utc", "'2026-01-01'")]
    public async Task LoadRejectsInvalidEntryValues(string column, string sqlValue)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await RemoveConstraintsAsync(temp.Path, "entries");
        await ExecuteAsync(temp.Path, $"UPDATE entries SET {column} = {sqlValue} WHERE id = 2");

        await AssertInvalidAsync(temp.Path);
    }

    [Theory]
    [InlineData("id", "NULL")]
    [InlineData("id", "'bad'")]
    [InlineData("path", "NULL")]
    [InlineData("path", "X'1234'")]
    [InlineData("reason", "NULL")]
    [InlineData("reason", "X'1234'")]
    public async Task LoadRejectsInvalidSkippedEntryValues(string column, string sqlValue)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await RemoveConstraintsAsync(temp.Path, "skipped_entries");
        await ExecuteAsync(temp.Path, $"UPDATE skipped_entries SET {column} = {sqlValue}");

        await AssertInvalidAsync(temp.Path);
    }

    [Theory]
    [InlineData("DELETE FROM entries")]
    [InlineData("UPDATE entries SET parent_id = 2 WHERE id = 1")]
    [InlineData("UPDATE entries SET parent_id = NULL WHERE id = 2")]
    [InlineData("UPDATE entries SET is_directory = 0, file_count = 1 WHERE id = 1")]
    [InlineData("UPDATE entries SET parent_id = 2 WHERE id = 3")]
    [InlineData("UPDATE entries SET file_count = 2 WHERE id = 2")]
    [InlineData("UPDATE entries SET directory_count = 1 WHERE id = 2")]
    [InlineData("UPDATE entries SET file_count = 3 WHERE id = 1")]
    [InlineData("UPDATE entries SET directory_count = 1 WHERE id = 1")]
    [InlineData("INSERT INTO entries SELECT * FROM entries WHERE id = 2")]
    public async Task LoadRejectsInvalidTopologyAndCounts(string mutation)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await RemoveConstraintsAsync(temp.Path, "entries");
        await ExecuteAsync(temp.Path, mutation);

        await AssertInvalidAsync(temp.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadRejectsDisconnectedDirectoryCycles(bool twoNodes)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, """
            DELETE FROM entries WHERE id != 1;
            UPDATE entries SET file_count = 0;
            INSERT INTO entries VALUES (2, 2, 'cycle', 'C:\cycle', 1, 0, 0, 0, 0, '', '', NULL);
            """);
        if (twoNodes)
        {
            await ExecuteAsync(temp.Path, """
                UPDATE entries SET parent_id = 3 WHERE id = 2;
                INSERT INTO entries VALUES (3, 2, 'cycle2', 'C:\cycle2', 1, 0, 0, 0, 0, '', '', NULL);
                """);
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ScanStore().LoadAsync(temp.Path));

        Assert.Contains("cyclic", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadRejectsDuplicateSkippedIdentifiers()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await RemoveConstraintsAsync(temp.Path, "skipped_entries");
        await ExecuteAsync(temp.Path, "INSERT INTO skipped_entries SELECT * FROM skipped_entries");

        await AssertInvalidAsync(temp.Path);
    }

    [Theory]
    [InlineData("portable_object_id", "NULL")]
    [InlineData("portable_object_id", "X'1234'")]
    [InlineData("portable_persistent_id", "NULL")]
    [InlineData("portable_persistent_id", "X'1234'")]
    public async Task LoadRejectsInvalidOptionalPortableValues(string column, string sqlValue)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, $"ALTER TABLE entries ADD COLUMN {column} TEXT; UPDATE entries SET {column} = ''; UPDATE entries SET {column} = {sqlValue} WHERE id = 2");

        await AssertInvalidAsync(temp.Path);
    }

    [Theory]
    [InlineData("portable_object_id")]
    [InlineData("portable_persistent_id")]
    public async Task LoadBoundsEachOptionalPortableColumn(string column)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, $"ALTER TABLE entries ADD COLUMN {column} TEXT NOT NULL DEFAULT ''; UPDATE entries SET {column} = $text WHERE id = 2", ("$text", new string('\u00e9', 33)));

        await AssertInvalidAsync(temp.Path, new ScanLoadLimits { MaxStringBytes = 64 });
    }

    [Fact]
    public async Task LoadRejectsIndexSizeOverflow()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, "UPDATE entries SET logical_size_bytes = 9223372036854775807 WHERE id IN (2, 3)");

        await AssertInvalidAsync(temp.Path);
    }

    [Fact]
    public async Task LoadEnforcesInclusiveFileSizeLimit()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        var bytes = new FileInfo(temp.Path).Length;

        await new ScanStore(new ScanLoadLimits { MaxFileBytes = bytes }).LoadAsync(temp.Path);
        await AssertInvalidAsync(temp.Path, new ScanLoadLimits { MaxFileBytes = bytes - 1 });
    }

    [Fact]
    public async Task LoadEnforcesInclusiveEntryCountLimit()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);

        var loaded = await new ScanStore(new ScanLoadLimits { MaxEntries = 3 }).LoadAsync(temp.Path);
        Assert.Equal(3, loaded.Root.Flatten().Count());
        await AssertInvalidAsync(temp.Path, new ScanLoadLimits { MaxEntries = 2 });
    }

    [Fact]
    public async Task LoadEnforcesInclusiveMetadataCountLimit()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);

        await new ScanStore(new ScanLoadLimits { MaxMetadataEntries = 5 }).LoadAsync(temp.Path);
        await AssertInvalidAsync(temp.Path, new ScanLoadLimits { MaxMetadataEntries = 4 });
    }

    [Fact]
    public async Task LoadEnforcesInclusiveSkippedEntryCountLimit()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, "INSERT INTO skipped_entries VALUES (2, 'C:\\locked2', 'Access denied')");

        var loaded = await new ScanStore(new ScanLoadLimits { MaxSkippedEntries = 2 }).LoadAsync(temp.Path);
        Assert.Equal(2, loaded.SkippedEntries.Count);
        await AssertInvalidAsync(temp.Path, new ScanLoadLimits { MaxSkippedEntries = 1 });
    }

    [Theory]
    [InlineData("UPDATE metadata SET value = $text WHERE key = 'engine'")]
    [InlineData("INSERT INTO metadata VALUES ($text, 'unknown metadata is allowed')")]
    [InlineData("UPDATE entries SET name = $text WHERE id = 2")]
    [InlineData("UPDATE entries SET full_path = $text WHERE id = 2")]
    [InlineData("UPDATE entries SET extension = $text WHERE id = 2")]
    [InlineData("UPDATE entries SET attributes = $text WHERE id = 2")]
    [InlineData("UPDATE skipped_entries SET path = $text")]
    [InlineData("UPDATE skipped_entries SET reason = $text")]
    public async Task LoadBoundsEveryStringFieldByUtf8Bytes(string mutation)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        var limits = new ScanLoadLimits { MaxStringBytes = 64 };
        await ExecuteAsync(temp.Path, mutation, ("$text", new string('\u00e9', 32)));

        await new ScanStore(limits).LoadAsync(temp.Path);
        if (mutation.StartsWith("INSERT", StringComparison.Ordinal))
        {
            await ExecuteAsync(temp.Path, "DELETE FROM metadata WHERE value = 'unknown metadata is allowed'");
        }
        await ExecuteAsync(temp.Path, mutation, ("$text", new string('\u00e9', 33)));
        await AssertInvalidAsync(temp.Path, limits);
    }

    [Fact]
    public async Task LoadCountsStringBytesAfterEmbeddedNull()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, "UPDATE entries SET name = $text WHERE id = 2", ("$text", "x\0" + new string('x', 64)));

        await AssertInvalidAsync(temp.Path, new ScanLoadLimits { MaxStringBytes = 64 });
    }

    [Fact]
    public async Task LoadEnforcesInclusiveTotalStringBudgetAcrossAllTables()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        var totalBytes = await TotalStringBytesAsync(temp.Path);

        await new ScanStore(new ScanLoadLimits { MaxTotalStringBytes = totalBytes }).LoadAsync(temp.Path);
        await AssertInvalidAsync(temp.Path, new ScanLoadLimits { MaxTotalStringBytes = totalBytes - 1 });
    }

    [Theory]
    [InlineData("UTF-16le")]
    [InlineData("UTF-16be")]
    public async Task LoadCountsUtf16AsciiStringsInUtf8Bytes(string encoding)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path, encoding);
        await ExecuteAsync(temp.Path, "UPDATE entries SET name = $text WHERE id = 2", ("$text", new string('x', 64)));

        var loaded = await new ScanStore(new ScanLoadLimits { MaxStringBytes = 64 }).LoadAsync(temp.Path);

        Assert.Equal(64, loaded.Root.Children[0].Name.Length);
    }

    [Theory]
    [InlineData("UTF-16le")]
    [InlineData("UTF-16be")]
    public async Task LoadCountsUtf16CjkStringsInUtf8Bytes(string encoding)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path, encoding);
        await ExecuteAsync(temp.Path, "UPDATE entries SET name = $text WHERE id = 2", ("$text", new string('\u6f22', 42)));
        var limits = new ScanLoadLimits { MaxStringBytes = 126 };

        await new ScanStore(limits).LoadAsync(temp.Path);
        await ExecuteAsync(temp.Path, "UPDATE entries SET name = $text WHERE id = 2", ("$text", new string('\u6f22', 43)));
        await AssertInvalidAsync(temp.Path, limits);
    }

    [Theory]
    [InlineData("UTF-16le", false)]
    [InlineData("UTF-16be", false)]
    [InlineData("UTF-16le", true)]
    [InlineData("UTF-16be", true)]
    public async Task LoadUsesSameTotalUtf8BudgetForEveryDatabaseEncoding(string encoding, bool cjk)
    {
        using var utf8 = new TempScanFile();
        using var utf16 = new TempScanFile();
        await CreateScanAsync(utf8.Path);
        await CreateScanAsync(utf16.Path, encoding);
        var text = new string(cjk ? '\u6f22' : 'x', 512);
        foreach (var path in new[] { utf8.Path, utf16.Path })
        {
            await ExecuteAsync(path, "UPDATE entries SET name = $text WHERE id = 2", ("$text", text));
        }
        var totalBytes = await TotalStringBytesAsync(utf8.Path);

        await new ScanStore(new ScanLoadLimits { MaxTotalStringBytes = totalBytes }).LoadAsync(utf16.Path);
        await AssertInvalidAsync(utf16.Path, new ScanLoadLimits { MaxTotalStringBytes = totalBytes - 1 });
    }

    [Fact]
    public async Task LoadEnforcesDepthWithRootAtOne()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, """
            INSERT INTO entries VALUES (4, 1, 'nested', 'C:\nested', 1, 30, 48, 2, 0, '', '', NULL);
            UPDATE entries SET parent_id = 4 WHERE id IN (2, 3);
            UPDATE entries SET directory_count = 1 WHERE id = 1;
            """);

        var loaded = await new ScanStore(new ScanLoadLimits { MaxDepth = 3 }).LoadAsync(temp.Path);
        Assert.Equal(2, Assert.Single(loaded.Root.Children).Children.Count);
        await AssertInvalidAsync(temp.Path, new ScanLoadLimits { MaxDepth = 2 });
    }

    [Fact]
    public async Task LoadAcceptsEmptyDirectoryAtDepthOne()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, "DELETE FROM entries WHERE id != 1; UPDATE entries SET file_count = 0");

        var loaded = await new ScanStore(new ScanLoadLimits { MaxDepth = 1 }).LoadAsync(temp.Path);
        Assert.Empty(loaded.Root.Children);
    }

    [Fact]
    public async Task LoadSupportsParentIdentifiersAfterTheirChildren()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, "UPDATE entries SET id = 100 WHERE id = 1; UPDATE entries SET parent_id = 100 WHERE parent_id = 1");

        var loaded = await new ScanStore().LoadAsync(temp.Path);

        Assert.Equal(@"C:\", loaded.Root.FullPath);
        Assert.Equal(new[] { "a.bin", "b.txt" }, loaded.Root.Children.Select(entry => entry.Name));
    }

    [Fact]
    public async Task LoadAllowsExplicitLargerDepthBudgetWithoutRecursiveValidation()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, """
            DELETE FROM entries;
            WITH RECURSIVE chain(id) AS (
                SELECT 1 UNION ALL SELECT id + 1 FROM chain WHERE id < 1050
            )
            INSERT INTO entries
            SELECT id, CASE WHEN id = 1 THEN NULL ELSE id - 1 END,
                'directory-' || id, 'C:\directory-' || id, 1, 0, 0, 0, 1050 - id, '', '', NULL
            FROM chain;
            """);
        await AssertInvalidAsync(temp.Path);

        var loaded = await new ScanStore(new ScanLoadLimits { MaxDepth = 1050 }).LoadAsync(temp.Path);
        var depth = 1;
        var current = loaded.Root;
        while (current.Children.Count != 0)
        {
            current = Assert.Single(current.Children);
            depth++;
        }

        Assert.Equal(1050, depth);
    }

    public static TheoryData<ScanLoadLimits> InvalidLimits => new()
    {
        new() { MaxFileBytes = 0 }, new() { MaxFileBytes = -1 },
        new() { MaxEntries = 0 }, new() { MaxEntries = -1 },
        new() { MaxSkippedEntries = 0 }, new() { MaxSkippedEntries = -1 },
        new() { MaxMetadataEntries = 0 }, new() { MaxMetadataEntries = -1 },
        new() { MaxStringBytes = 0 }, new() { MaxStringBytes = -1 },
        new() { MaxTotalStringBytes = 0 }, new() { MaxTotalStringBytes = -1 },
        new() { MaxDepth = 0 }, new() { MaxDepth = -1 }
    };

    [Theory]
    [MemberData(nameof(InvalidLimits))]
    public void ConstructorRejectsNonpositiveLimits(ScanLoadLimits limits)
        => Assert.ThrowsAny<ArgumentException>(() => new ScanStore(limits));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LegacyFixtureLoadsAndRoundTripsWithIndependentPortableColumns(bool objectId, bool persistentId)
    {
        using var original = new TempScanFile();
        using var roundTrip = new TempScanFile();
        await CreateScanAsync(original.Path);
        if (objectId)
        {
            await ExecuteAsync(original.Path, "ALTER TABLE entries ADD COLUMN portable_object_id TEXT NOT NULL DEFAULT 'object-id'");
        }
        if (persistentId)
        {
            await ExecuteAsync(original.Path, "ALTER TABLE entries ADD COLUMN portable_persistent_id TEXT NOT NULL DEFAULT 'persistent-id'");
        }
        var store = new ScanStore();

        var loaded = await store.LoadAsync(original.Path);
        Assert.Equal(ScanSourceKind.FileSystem, loaded.SourceKind);
        Assert.Equal(@"C:\", loaded.SourceId);
        Assert.Equal(@"C:\", loaded.DisplayRootPath);
        Assert.Equal(999, loaded.Root.LogicalSizeBytes); // Historical directory sizes need not add up to children.
        Assert.Equal(2, loaded.Root.Children.Count);
        Assert.Equal(objectId ? "object-id" : "", loaded.Root.PortableObjectId);
        Assert.Equal(persistentId ? "persistent-id" : "", loaded.Root.PortablePersistentId);
        Assert.Equal(30, loaded.GlobalIndex!.TotalFileLogicalSizeBytes);
        Assert.Equal(2, loaded.GlobalIndex.FileEntryCount);
        Assert.Single(loaded.SkippedEntries);
        await store.SaveAsync(loaded, roundTrip.Path);
        var reloaded = await store.LoadAsync(roundTrip.Path);

        Assert.Equal(loaded.Root.Flatten().Select(e => e.FullPath), reloaded.Root.Flatten().Select(e => e.FullPath));
        Assert.Equal(loaded.Root.PortableObjectId, reloaded.Root.PortableObjectId);
        Assert.Equal(loaded.Root.PortablePersistentId, reloaded.Root.PortablePersistentId);
        Assert.Equal(loaded.SkippedEntries, reloaded.SkippedEntries);
        Assert.Equal(loaded.StartedAt, reloaded.StartedAt);
        Assert.Equal(loaded.CompletedAt, reloaded.CompletedAt);
    }

    [Fact]
    public async Task LoadHonorsPreCancelledTokenAndReleasesFile()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ScanStore().LoadAsync(temp.Path, cancellation.Token));
        AssertExclusiveOpen(temp.Path);
    }

    [Fact]
    public async Task LoadHonorsCancellationAfterOpeningTheScanAndReleasesFile()
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        await ExecuteAsync(temp.Path, """
            DELETE FROM entries WHERE id != 1;
            UPDATE entries SET file_count = 50000;
            WITH RECURSIVE files(id) AS (
                SELECT 2 UNION ALL SELECT id + 1 FROM files WHERE id < 50001
            )
            INSERT INTO entries
            SELECT id, 1, 'file-' || id, 'C:\file-' || id, 0, 1, 1, 1, 0, '', '', NULL
            FROM files;
            """);
        using var cancellation = new CancellationTokenSource();
        var load = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    return await new ScanStore().LoadAsync(temp.Path, cancellation.Token);
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) == 32 && !cancellation.IsCancellationRequested)
                {
                    // The read-share probe can briefly win the opening race. Retry
                    // only that sharing violation until the loader owns its handle.
                    await Task.Yield();
                }
            }
        });
        var openedByLoader = false;
        var timer = Stopwatch.StartNew();
        try
        {
            // Observe the loader's read-only file share before cancelling. This avoids
            // relying on a timer to guess when loading has actually started.
            while (!load.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                try
                {
                    using var probe = new FileStream(temp.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) == 32)
                {
                    openedByLoader = true;
                    break;
                }

                await Task.Delay(1);
            }

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(openedByLoader, "The loader must enter the file before cancellation is requested.");
            AssertExclusiveOpen(temp.Path);
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadIsReadOnlyAndReleasesFileAfterSuccessOrRejection(bool malformed)
    {
        using var temp = new TempScanFile();
        await CreateScanAsync(temp.Path);
        if (malformed)
        {
            await ExecuteAsync(temp.Path, "UPDATE entries SET parent_id = 999 WHERE id = 2");
        }
        var before = await File.ReadAllBytesAsync(temp.Path);

        if (malformed)
        {
            await AssertInvalidAsync(temp.Path);
        }
        else
        {
            await new ScanStore().LoadAsync(temp.Path);
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(temp.Path));
        AssertExclusiveOpen(temp.Path);
    }

    [Fact]
    public async Task LoadRejectsDeterministicMalformedBinaryCorpus()
    {
        using var valid = new TempScanFile();
        await CreateScanAsync(valid.Path);
        var bytes = await File.ReadAllBytesAsync(valid.Path);
        var random = new Random(53);
        var corpus = new List<byte[]> { Array.Empty<byte>(), Encoding.UTF8.GetBytes("not a SQLite scan"), bytes[..100], bytes[..(bytes.Length / 2)] };
        for (var i = 0; i < 8; i++)
        {
            var noise = new byte[64 + i * 31];
            random.NextBytes(noise);
            corpus.Add(noise);
        }

        foreach (var malformed in corpus)
        {
            using var temp = new TempScanFile();
            await File.WriteAllBytesAsync(temp.Path, malformed);
            await AssertInvalidAsync(temp.Path);
            AssertExclusiveOpen(temp.Path);
        }
    }

    private static async Task AssertInvalidAsync(string path, ScanLoadLimits? limits = null)
    {
        var store = limits is null ? new ScanStore() : new ScanStore(limits);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(path));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    private static void AssertExclusiveOpen(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(file.CanRead);
    }

    private static async Task RemoveConstraintsAsync(string path, string table)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ConnectionString);
        await connection.OpenAsync();
        var columns = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add($"{reader.GetString(1)} {reader.GetString(2)}");
            }
        }

        await using var rebuild = connection.CreateCommand();
        rebuild.CommandText = $"""
            CREATE TABLE replacement ({string.Join(", ", columns)});
            INSERT INTO replacement SELECT * FROM {table};
            DROP TABLE {table};
            ALTER TABLE replacement RENAME TO {table};
            """;
        await rebuild.ExecuteNonQueryAsync();
    }

    private static async Task<long> TotalStringBytesAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT SUM(length(CAST(key AS BLOB)) + length(CAST(value AS BLOB))) FROM metadata)
                + (SELECT SUM(length(CAST(name AS BLOB)) + length(CAST(full_path AS BLOB))
                    + length(CAST(extension AS BLOB)) + length(CAST(attributes AS BLOB))
                    + COALESCE(length(CAST(last_write_utc AS BLOB)), 0)) FROM entries)
                + (SELECT SUM(length(CAST(path AS BLOB)) + length(CAST(reason AS BLOB))) FROM skipped_entries)
            """;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static Task CreateScanAsync(string path, string encoding = "UTF-8") => ExecuteAsync(path, $"PRAGMA encoding = '{encoding}';" + """
        CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT INTO metadata VALUES
            ('format', 'custodian-scan-v1'),
            ('root_path', 'C:\'),
            ('engine', 'Fixture'),
            ('started_at', '2026-01-02T03:04:05.0000000+00:00'),
            ('completed_at', '2026-01-02T03:05:06.0000000+00:00');
        CREATE TABLE entries (
            id INTEGER PRIMARY KEY,
            parent_id INTEGER NULL,
            name TEXT NOT NULL,
            full_path TEXT NOT NULL,
            is_directory INTEGER NOT NULL,
            logical_size_bytes INTEGER NOT NULL,
            allocated_size_bytes INTEGER NOT NULL,
            file_count INTEGER NOT NULL,
            directory_count INTEGER NOT NULL,
            extension TEXT NOT NULL,
            attributes TEXT NOT NULL,
            last_write_utc TEXT NULL
        );
        INSERT INTO entries VALUES
            (1, NULL, 'C:', 'C:\', 1, 999, 999, 2, 0, '', '', NULL),
            (2, 1, 'a.bin', 'C:\a.bin', 0, 10, 16, 1, 0, '.bin', 'Archive', '2026-01-01T00:00:00.0000000Z'),
            (3, 1, 'b.txt', 'C:\b.txt', 0, 20, 32, 1, 0, '.txt', '', NULL);
        CREATE TABLE skipped_entries (id INTEGER PRIMARY KEY, path TEXT NOT NULL, reason TEXT NOT NULL);
        INSERT INTO skipped_entries VALUES (1, 'C:\locked', 'Access denied');
        """);

    private static async Task ExecuteAsync(string path, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TempScanFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"custodian-validation-{Guid.NewGuid():N}.custodian-scan");

        public void Dispose() => File.Delete(Path);
    }
}
