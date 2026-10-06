using System.Globalization;
using Custodian.Core.Analysis;
using Custodian.Core.Model;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Custodian.Core.Storage;

// All passes share a read transaction. Validate resource use and topology before
// allocating FileSystemEntry objects, child lists, or the global index.
internal sealed class SavedScanReader(SqliteConnection connection, ScanLoadLimits limits, CancellationToken cancellationToken)
{
    private SqliteTransaction? _transaction;
    private long _textBytes;

    public async Task<ScanResult> ReadAsync()
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            // Bound SQLite's own row/schema allocations, including columns we do not select.
            raw.sqlite3_limit(connection.Handle, raw.SQLITE_LIMIT_LENGTH,
                (int)Math.Min(int.MaxValue, Math.Max(1_048_576L, 16L * limits.MaxStringBytes + 1_024)));
            raw.sqlite3_limit(connection.Handle, raw.SQLITE_LIMIT_COLUMN, 64);
            raw.sqlite3_progress_handler(connection.Handle, 1_000,
                _ => cancellationToken.IsCancellationRequested ? 1 : 0, null);
            // sqlite3_value_bytes measures UTF-8 even in UTF-16 databases, includes
            // embedded NUL tails, and avoids allocating an untrusted managed string.
            var registration = raw.sqlite3_create_function(connection.Handle, "custodian_utf8_bytes", 1,
                raw.SQLITE_UTF8 | raw.SQLITE_DETERMINISTIC | 0x00080000 /* SQLITE_DIRECTONLY */, null,
                (context, _, values) => raw.sqlite3_result_int64(context, raw.sqlite3_value_bytes(values[0])));
            if (registration != raw.SQLITE_OK)
            {
                throw Invalid("Could not initialize saved scan string validation.");
            }

            await using (var setup = Command("PRAGMA trusted_schema=OFF; PRAGMA query_only=ON; PRAGMA cache_size=-8192;"))
            {
                await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using var transaction = connection.BeginTransaction(deferred: true);
            _transaction = transaction;
            await ValidateTableAsync("metadata", ["key", "value"], []).ConfigureAwait(false);
            var columns = await ValidateTableAsync("entries",
                ["name", "full_path", "extension", "attributes", "last_write_utc"],
                ["id", "parent_id", "is_directory", "logical_size_bytes", "allocated_size_bytes", "file_count", "directory_count"]).ConfigureAwait(false);
            await ValidateTableAsync("skipped_entries", ["path", "reason"], ["id"]).ConfigureAwait(false);

            var metadata = await ReadMetadataAsync().ConfigureAwait(false);
            var result = ReadResultMetadata(metadata);
            var textColumns = new List<string> { "name", "full_path", "extension", "attributes", "last_write_utc" };
            foreach (var optional in new[] { "portable_object_id", "portable_persistent_id" })
            {
                if (columns.Contains(optional))
                {
                    textColumns.Add(optional);
                }
            }

            var topology = await PreflightEntriesAsync(textColumns).ConfigureAwait(false);
            var rootId = ValidateTopology(topology);
            await PreflightSkippedAsync().ConfigureAwait(false);

            var entries = await MaterializeEntriesAsync(columns).ConfigureAwait(false);
            var index = new ScanGlobalIndexBuilder();
            foreach (var (id, entry) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = topology[id];
                if (node.ParentId is { } parentId)
                {
                    entries[parentId].Children.Add(entry);
                }

                index.Observe(entry);
            }

            result.Root = entries[rootId];
            result.GlobalIndex = index.Build(result.Root);
            await using (var command = Command("SELECT path, reason FROM skipped_entries ORDER BY id"))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.SkippedEntries.Add(new SkippedEntry(reader.GetString(0), reader.GetString(1)));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException("The saved scan database is malformed or exceeds a SQLite resource limit.", ex);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("Saved scan counters exceed the supported range.", ex);
        }
        finally
        {
            _transaction = null;
            if (connection.State == System.Data.ConnectionState.Open)
            {
                raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
            }
        }
    }

    private SqliteCommand Command(string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        return command;
    }

    private async Task<HashSet<string>> ValidateTableAsync(string table, string[] textColumns, string[] integerColumns)
    {
        await using (var command = Command("SELECT type FROM pragma_table_list WHERE schema='main' AND name=$table"))
        {
            command.Parameters.AddWithValue("$table", table);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string type || type != "table")
            {
                throw Invalid($"Required table '{table}' is missing or is not an ordinary table.");
            }
        }

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var info = Command($"PRAGMA table_xinfo({table})");
        await using var reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = reader.GetString(1);
            var type = reader.GetString(2);
            if (reader.GetInt32(6) != 0)
            {
                throw Invalid($"Generated or hidden columns are not supported in '{table}'.");
            }

            var isText = textColumns.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                (table == "entries" && (name.Equals("portable_object_id", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("portable_persistent_id", StringComparison.OrdinalIgnoreCase)));
            if ((isText && !type.Equals("TEXT", StringComparison.OrdinalIgnoreCase)) ||
                (integerColumns.Contains(name, StringComparer.OrdinalIgnoreCase) && !type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase)))
            {
                throw Invalid($"Column '{table}.{name}' has an unsupported declared type.");
            }

            columns.Add(name);
        }

        if (textColumns.Concat(integerColumns).Any(name => !columns.Contains(name)))
        {
            throw Invalid($"Required columns are missing from '{table}'.");
        }

        return columns;
    }

    // Only scalar lengths/types cross into managed memory during the budget pass.
    private static string TextChecks(IEnumerable<string> columns)
        => string.Join(", ", columns.Select(column => $"typeof({column}), custodian_utf8_bytes({column})"));

    private void CheckText(SqliteDataReader reader, int ordinal, bool nullable = false)
    {
        var type = reader.GetString(ordinal);
        if (nullable && type == "null")
        {
            return;
        }

        if (type != "text")
        {
            throw Invalid("A saved scan text value has an invalid storage type or is NULL.");
        }

        var bytes = reader.GetInt64(ordinal + 1);
        if (bytes > limits.MaxStringBytes)
        {
            throw Invalid("A saved scan string exceeds the configured byte limit.");
        }

        _textBytes = checked(_textBytes + bytes);
        if (_textBytes > limits.MaxTotalStringBytes)
        {
            throw Invalid("Saved scan strings exceed the configured total byte limit.");
        }
    }

    private async Task<Dictionary<string, string>> ReadMetadataAsync()
    {
        var count = 0;
        await using (var command = Command($"SELECT {TextChecks(["key", "value"])} FROM metadata"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > limits.MaxMetadataEntries)
                {
                    throw Invalid("Saved scan metadata exceeds the configured count limit.");
                }

                CheckText(reader, 0);
                CheckText(reader, 2);
            }
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var values = Command("SELECT key, value FROM metadata");
        await using var valueReader = await values.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await valueReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!metadata.TryAdd(valueReader.GetString(0), valueReader.GetString(1)))
            {
                throw Invalid("Saved scan metadata contains duplicate keys.");
            }
        }

        return metadata;
    }

    private static ScanResult ReadResultMetadata(Dictionary<string, string> metadata)
    {
        foreach (var key in new[] { "format", "root_path", "engine", "started_at", "completed_at" })
        {
            if (!metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw Invalid($"Saved scan metadata '{key}' is required.");
            }
        }

        if (metadata["format"] != "custodian-scan-v1")
        {
            throw Invalid("Unsupported saved scan format/version.");
        }

        var sourceKind = ScanSourceKind.FileSystem;
        if (metadata.TryGetValue("source_kind", out var source) &&
            (!Enum.TryParse(source, ignoreCase: true, out sourceKind) || !Enum.IsDefined(sourceKind)))
        {
            throw Invalid("Saved scan source kind is invalid.");
        }

        var started = ParseDate(metadata["started_at"]);
        var completed = ParseDate(metadata["completed_at"]);
        if (completed < started)
        {
            throw Invalid("Saved scan completion precedes its start.");
        }

        var result = new ScanResult
        {
            RootPath = metadata["root_path"],
            Engine = metadata["engine"],
            StartedAt = started,
            CompletedAt = completed,
            SourceKind = sourceKind,
            SourceId = metadata.GetValueOrDefault("source_id") ?? metadata["root_path"],
            DisplayRootPath = metadata.GetValueOrDefault("display_root_path") ?? metadata["root_path"],
            PortableDeviceId = metadata.GetValueOrDefault("portable_device_id") ?? string.Empty,
            PortableStorageObjectId = metadata.GetValueOrDefault("portable_storage_object_id") ?? string.Empty,
            PortableDeviceName = metadata.GetValueOrDefault("portable_device_name") ?? string.Empty,
            PortableStorageName = metadata.GetValueOrDefault("portable_storage_name") ?? string.Empty
        };
        var providerId = metadata.GetValueOrDefault("cloud_provider_id") ?? string.Empty;
        var providerName = metadata.GetValueOrDefault("cloud_provider_name") ?? string.Empty;
        var providerRoot = metadata.GetValueOrDefault("cloud_provider_root_path") ?? string.Empty;
        var account = metadata.GetValueOrDefault("cloud_provider_account_label") ?? string.Empty;
        if (new[] { providerId, providerName, providerRoot, account }.Any(value => value.Length > 0))
        {
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(providerRoot))
            {
                throw Invalid("Saved scan cloud provider metadata is incomplete.");
            }

            result.CloudProvider = new CloudProviderMetadata(providerId, providerName, account, providerRoot);
        }

        return result;
    }

    private async Task<Dictionary<long, Node>> PreflightEntriesAsync(List<string> textColumns)
    {
        string[] integers = ["id", "parent_id", "is_directory", "logical_size_bytes", "allocated_size_bytes", "file_count", "directory_count"];
        var nodes = new Dictionary<long, Node>();
        long fileBytes = 0, allocatedFileBytes = 0, folderBytes = 0;
        await using var command = Command($"SELECT {string.Join(", ", integers.Select(column => $"typeof({column}), {column}"))}, {TextChecks(textColumns)} FROM entries");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nodes.Count >= limits.MaxEntries)
            {
                throw Invalid("Saved scan entries exceed the configured count limit.");
            }

            for (var i = 0; i < integers.Length; i++)
            {
                if (reader.GetString(i * 2) != "integer" && !(i == 1 && reader.IsDBNull(i * 2 + 1)))
                {
                    throw Invalid("A saved scan integer value has an invalid storage type or is NULL.");
                }
            }

            var id = reader.GetInt64(1);
            long? parent = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            var directory = reader.GetInt64(5);
            var logical = reader.GetInt64(7);
            var allocated = reader.GetInt64(9);
            var files = reader.GetInt64(11);
            var directories = reader.GetInt64(13);
            if (id <= 0 || parent <= 0 || directory is not (0 or 1) || logical < 0 || allocated < 0 || files < 0 || directories < 0 ||
                (directory == 0 && (files != 1 || directories != 0)))
            {
                throw Invalid("Saved scan entry identifiers, flags, sizes, or counters are inconsistent.");
            }

            for (var i = 0; i < textColumns.Count; i++)
            {
                CheckText(reader, 14 + i * 2, nullable: textColumns[i] == "last_write_utc");
            }

            if (!nodes.TryAdd(id, new Node(parent, directory == 1, files, directories)))
            {
                throw Invalid("Saved scan contains duplicate entry identifiers.");
            }

            // Protect the existing index's long accumulators, including nested folder totals.
            if (directory == 1)
            {
                folderBytes = checked(folderBytes + logical);
            }
            else
            {
                fileBytes = checked(fileBytes + logical);
                allocatedFileBytes = checked(allocatedFileBytes + allocated);
            }
        }

        return nodes;
    }

    private long ValidateTopology(Dictionary<long, Node> nodes)
    {
        long? root = null;
        foreach (var (id, node) in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.ParentId is not { } parentId)
            {
                if (root is not null || !node.IsDirectory)
                {
                    throw Invalid("Saved scan must have exactly one directory root.");
                }

                root = id;
            }
            else
            {
                if (!nodes.TryGetValue(parentId, out var parent) || !parent.IsDirectory)
                {
                    throw Invalid("Saved scan has an orphan entry or a file used as a parent.");
                }

                parent.ChildFiles = checked(parent.ChildFiles + node.Files);
                parent.ChildDirectories = checked(parent.ChildDirectories + (node.IsDirectory ? node.Directories + 1 : 0));
            }
        }

        if (root is null)
        {
            throw Invalid("Saved scan has no directory root.");
        }

        // Iterative parent-chain marking is linear and cannot overflow the call stack.
        var chain = new List<Node>();
        foreach (var node in nodes.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = node;
            while (current.Depth == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (current.Visiting)
                {
                    throw Invalid("Saved scan has cyclic or disconnected entries.");
                }

                current.Visiting = true;
                chain.Add(current);
                if (chain.Count > limits.MaxDepth)
                {
                    throw Invalid("Saved scan exceeds the configured depth limit.");
                }

                if (current.ParentId is not { } parentId)
                {
                    break;
                }

                current = nodes[parentId];
            }

            var depth = current.Depth;
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++depth > limits.MaxDepth)
                {
                    throw Invalid("Saved scan exceeds the configured depth limit.");
                }

                chain[i].Depth = depth;
                chain[i].Visiting = false;
            }

            chain.Clear();
            if (node.IsDirectory && (node.ChildFiles != node.Files || node.ChildDirectories != node.Directories))
            {
                throw Invalid("Saved scan directory counters do not match its children.");
            }
        }

        return root.Value;
    }

    private async Task PreflightSkippedAsync()
    {
        var count = 0;
        var ids = new HashSet<long>();
        await using var command = Command($"SELECT typeof(id), id, {TextChecks(["path", "reason"])} FROM skipped_entries");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > limits.MaxSkippedEntries)
            {
                throw Invalid("Saved scan skipped entries exceed the configured count limit.");
            }

            if (reader.GetString(0) != "integer" || reader.GetInt64(1) <= 0 || !ids.Add(reader.GetInt64(1)))
            {
                throw Invalid("Saved scan skipped entry identifiers are invalid or duplicated.");
            }

            CheckText(reader, 2);
            CheckText(reader, 4);
        }
    }

    private async Task<Dictionary<long, FileSystemEntry>> MaterializeEntriesAsync(HashSet<string> columns)
    {
        var objectId = columns.Contains("portable_object_id") ? "portable_object_id" : "''";
        var persistentId = columns.Contains("portable_persistent_id") ? "portable_persistent_id" : "''";
        var entries = new Dictionary<long, FileSystemEntry>();
        await using var command = Command($"""
            SELECT id, name, full_path, is_directory, logical_size_bytes, allocated_size_bytes,
                file_count, directory_count, extension, attributes, last_write_utc, {objectId}, {persistentId}
            FROM entries ORDER BY id
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = reader.GetString(2);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw Invalid("Saved scan entries require a full path.");
            }

            entries.Add(reader.GetInt64(0), new FileSystemEntry
            {
                Name = reader.GetString(1),
                FullPath = path,
                IsDirectory = reader.GetInt64(3) == 1,
                LogicalSizeBytes = reader.GetInt64(4),
                AllocatedSizeBytes = reader.GetInt64(5),
                FileCount = reader.GetInt64(6),
                DirectoryCount = reader.GetInt64(7),
                Extension = reader.GetString(8),
                Attributes = reader.GetString(9),
                LastWriteTime = reader.IsDBNull(10) ? null : ParseDate(reader.GetString(10)),
                PortableObjectId = reader.GetString(11),
                PortablePersistentId = reader.GetString(12)
            });
        }

        return entries;
    }

    private static DateTimeOffset ParseDate(string value)
    {
        if (!DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
        {
            throw Invalid("Saved scan contains an invalid timestamp.");
        }

        return date;
    }

    private static InvalidDataException Invalid(string message) => new(message);

    private sealed class Node(long? parentId, bool isDirectory, long files, long directories)
    {
        public long? ParentId { get; } = parentId;
        public bool IsDirectory { get; } = isDirectory;
        public long Files { get; } = files;
        public long Directories { get; } = directories;
        public long ChildFiles { get; set; }
        public long ChildDirectories { get; set; }
        public int Depth { get; set; }
        public bool Visiting { get; set; }
    }
}
