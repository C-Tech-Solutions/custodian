using Custodian.Core.Model;
using Custodian.Core.Analysis;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Custodian.Core.Storage;

public sealed class ScanStore
{
    // A scan file can come from anywhere, so the loader reads only these ordinary tables and
    // refuses views, virtual tables and generated columns, whose reads run code from the file.
    private static readonly string[] LoadedTables = ["metadata", "entries", "skipped_entries"];

    public async Task SaveAsync(ScanResult result, string path, CancellationToken cancellationToken = default)
    {
        var tempPath = CreateTemporarySavePath(path);
        try
        {
            await SaveCoreAsync(result, tempPath, cancellationToken).ConfigureAwait(false);
            ReplaceSavedScanFile(tempPath, path);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static async Task SaveCoreAsync(ScanResult result, string path, CancellationToken cancellationToken)
    {
        // Pooling is disabled so the file handle is released as soon as the connection is
        // disposed. With the default pool the handle lingers, causing an external
        // rename/overwrite after save to fail with a sharing violation.
        await using var connection = CreateConnection(path, SqliteOpenMode.ReadWriteCreate);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ApplySavePragmasAsync(connection, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, """
            CREATE TABLE metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
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
                last_write_utc TEXT NULL,
                portable_object_id TEXT NOT NULL DEFAULT '',
                portable_persistent_id TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE skipped_entries (
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL,
                reason TEXT NOT NULL
            );
            """, cancellationToken).ConfigureAwait(false);

        var sourceId = string.IsNullOrWhiteSpace(result.SourceId) ? result.RootPath : result.SourceId;
        var displayRootPath = string.IsNullOrWhiteSpace(result.DisplayRootPath) ? result.RootPath : result.DisplayRootPath;
        await InsertMetadataAsync(connection, "format", ScanFormat, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "root_path", result.RootPath, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "source_kind", result.SourceKind.ToString(), cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "source_id", sourceId, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "display_root_path", displayRootPath, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "portable_device_id", result.PortableDeviceId, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "portable_storage_object_id", result.PortableStorageObjectId, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "portable_device_name", result.PortableDeviceName, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "portable_storage_name", result.PortableStorageName, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "cloud_provider_id", result.CloudProvider?.ProviderId ?? string.Empty, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "cloud_provider_name", result.CloudProvider?.ProviderName ?? string.Empty, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "cloud_provider_account_label", result.CloudProvider?.AccountLabel ?? string.Empty, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "cloud_provider_root_path", result.CloudProvider?.RootPath ?? string.Empty, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "engine", result.Engine, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "started_at", result.StartedAt.ToString("O", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "completed_at", result.CompletedAt.ToString("O", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var entryCommand = CreateInsertEntryCommand(connection, transaction);
        entryCommand.Prepare();
        await InsertEntriesAsync(entryCommand, result.Root, cancellationToken).ConfigureAwait(false);

        foreach (var skipped in result.SkippedEntries)
        {
            await InsertSkippedAsync(connection, transaction, skipped, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScanResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(path, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await EnsureOrdinaryTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        var metadata = await LoadMetadataAsync(connection, cancellationToken).ConfigureAwait(false);
        EnsureScanFormat(metadata);
        var rootPath = RequireMetadata(metadata, "root_path");
        var engine = RequireMetadata(metadata, "engine");
        var startedAt = ParseTimestamp(RequireMetadata(metadata, "started_at"), "Scan file start time");
        var completedAt = ParseTimestamp(RequireMetadata(metadata, "completed_at"), "Scan file completion time");

        var entryColumns = await LoadTableColumnsAsync(connection, "entries", cancellationToken).ConfigureAwait(false);
        var textColumns = new List<string> { "name", "full_path", "extension", "attributes" };
        var portableObjectIdSelect = "''";
        var portablePersistentIdSelect = "''";
        if (entryColumns.Contains("portable_object_id"))
        {
            portableObjectIdSelect = "portable_object_id";
            textColumns.Add(portableObjectIdSelect);
        }

        if (entryColumns.Contains("portable_persistent_id"))
        {
            portablePersistentIdSelect = "portable_persistent_id";
            textColumns.Add(portablePersistentIdSelect);
        }

        var byId = new Dictionary<long, FileSystemEntry>();
        var parentById = new Dictionary<long, long?>();
        var indexBuilder = new ScanGlobalIndexBuilder();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT id, parent_id, name, full_path, is_directory, logical_size_bytes, allocated_size_bytes, file_count, directory_count, extension, attributes, last_write_utc, {portableObjectIdSelect}, {portablePersistentIdSelect},
                    {EntryTypesMatchExpression(textColumns)}
                FROM entries
                ORDER BY id
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetInt64(14) == 0)
                {
                    throw new InvalidDataException("Scan file entries contain a missing or wrongly typed value.");
                }

                var id = reader.GetInt64(0);
                long? parentId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                var isDirectory = reader.GetInt64(4);
                var entry = new FileSystemEntry
                {
                    Name = reader.GetString(2),
                    FullPath = reader.GetString(3),
                    IsDirectory = isDirectory != 0,
                    LogicalSizeBytes = reader.GetInt64(5),
                    AllocatedSizeBytes = reader.GetInt64(6),
                    FileCount = reader.GetInt64(7),
                    DirectoryCount = reader.GetInt64(8),
                    Extension = reader.GetString(9),
                    Attributes = reader.GetString(10),
                    LastWriteTime = reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11), $"Scan file entry {id} last-write time"),
                    PortableObjectId = reader.GetString(12),
                    PortablePersistentId = reader.GetString(13)
                };
                if (isDirectory is not (0 or 1) ||
                    entry.LogicalSizeBytes < 0 ||
                    entry.AllocatedSizeBytes < 0 ||
                    entry.FileCount < 0 ||
                    entry.DirectoryCount < 0 ||
                    string.IsNullOrWhiteSpace(entry.FullPath))
                {
                    throw new InvalidDataException(
                        $"Scan file entry {id} has a negative size or count, an invalid folder flag, or no full path.");
                }

                if (!byId.TryAdd(id, entry))
                {
                    throw new InvalidDataException($"Scan file contains duplicate entry ID {id}.");
                }

                parentById[id] = parentId;
                indexBuilder.Observe(entry);
            }
        }

        var root = LinkEntryTree(byId, parentById);

        var result = new ScanResult
        {
            RootPath = rootPath,
            SourceKind = ParseSourceKind(metadata.GetValueOrDefault("source_kind")),
            SourceId = metadata.GetValueOrDefault("source_id") ?? rootPath,
            DisplayRootPath = metadata.GetValueOrDefault("display_root_path") ?? rootPath,
            PortableDeviceId = metadata.GetValueOrDefault("portable_device_id") ?? string.Empty,
            PortableStorageObjectId = metadata.GetValueOrDefault("portable_storage_object_id") ?? string.Empty,
            PortableDeviceName = metadata.GetValueOrDefault("portable_device_name") ?? string.Empty,
            PortableStorageName = metadata.GetValueOrDefault("portable_storage_name") ?? string.Empty,
            CloudProvider = LoadCloudProviderMetadata(metadata),
            Engine = engine,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Root = root,
            GlobalIndex = indexBuilder.Build(root)
        };

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT path, reason, typeof(path) = 'text' AND typeof(reason) = 'text' FROM skipped_entries ORDER BY id";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetInt64(2) == 0)
                {
                    throw new InvalidDataException("Scan file skipped entries contain a missing or wrongly typed value.");
                }

                result.SkippedEntries.Add(new SkippedEntry(reader.GetString(0), reader.GetString(1)));
            }
        }

        return result;
    }

    /// <summary>
    /// Links the loaded entries into one tree and returns its root. Throws unless the rows form
    /// exactly one tree: a single root, every parent present, and every entry reachable from the
    /// root (an entry that is not is part of, or hangs below, a parent cycle).
    /// </summary>
    private static FileSystemEntry LinkEntryTree(
        Dictionary<long, FileSystemEntry> byId,
        Dictionary<long, long?> parentById)
    {
        FileSystemEntry? root = null;
        foreach (var (id, entry) in byId)
        {
            if (parentById[id] is not { } parentId)
            {
                if (root is not null)
                {
                    throw new InvalidDataException("Multiple root folders found in scan file.");
                }

                root = entry;
                continue;
            }

            if (!byId.TryGetValue(parentId, out var parent))
            {
                throw new InvalidDataException($"Scan file entry {id} refers to missing parent entry {parentId}.");
            }

            parent.Children.Add(entry);
        }

        if (root is null)
        {
            throw new InvalidDataException("No root folder found in scan file.");
        }

        // Each entry has exactly one parent, so a cycle can never be reached from the root and
        // this walk always ends. Any entry it does not reach is in, or below, a cycle.
        var reachable = root.Flatten().Count();
        if (reachable != byId.Count)
        {
            throw new InvalidDataException("Scan file entries contain a parent cycle.");
        }

        return root;
    }

    private const string ScanFormat = "custodian-scan-v1";

    private static void EnsureScanFormat(IReadOnlyDictionary<string, string> metadata)
    {
        // Every version has written this marker, so a file without it is not a Custodian scan.
        if (!metadata.TryGetValue("format", out var format))
        {
            throw new InvalidDataException("This file is not a Custodian scan.");
        }

        if (!string.Equals(format, ScanFormat, StringComparison.Ordinal))
        {
            throw new InvalidDataException("This scan file uses a format this version of Custodian can't open.");
        }
    }

    private static string RequireMetadata(IReadOnlyDictionary<string, string> metadata, string key)
        => metadata.TryGetValue(key, out var value)
            ? value
            : throw new InvalidDataException($"Scan file metadata is missing '{key}'.");

    private static DateTimeOffset ParseTimestamp(string value, string description)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
            ? timestamp
            : throw new InvalidDataException($"{description} is not a valid timestamp.");

    // SQLite keeps whatever type a value was written with, whatever the column declares, and the
    // reader quietly turns text read as a number into 0. So each row reports whether every value
    // has the type Custodian writes, and the loader checks that before reading the row.
    private static string EntryTypesMatchExpression(IEnumerable<string> textColumns)
    {
        string[] integerColumns = ["id", "is_directory", "logical_size_bytes", "allocated_size_bytes", "file_count", "directory_count"];
        var checks = integerColumns.Select(column => $"typeof({column}) = 'integer'")
            .Concat(textColumns.Select(column => $"typeof({column}) = 'text'"))
            .Append("typeof(parent_id) IN ('integer', 'null')")
            .Append("typeof(last_write_utc) IN ('text', 'null')");
        return string.Join(" AND ", checks);
    }

    private static string CreateTemporarySavePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }

        return Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
    }

    private static void ReplaceSavedScanFile(string tempPath, string path)
    {
        if (File.Exists(path))
        {
            File.Replace(tempPath, path, destinationBackupFileName: null);
            return;
        }

        File.Move(tempPath, path);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; a leftover temp file is safer than masking the save failure.
        }
    }

    private static ScanSourceKind ParseSourceKind(string? value)
    {
        // TryParse also accepts any number, so the result must be a defined kind.
        return Enum.TryParse<ScanSourceKind>(value, ignoreCase: true, out var sourceKind) && Enum.IsDefined(sourceKind)
            ? sourceKind
            : ScanSourceKind.FileSystem;
    }

    private static CloudProviderMetadata? LoadCloudProviderMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        var providerId = metadata.GetValueOrDefault("cloud_provider_id") ?? string.Empty;
        var providerName = metadata.GetValueOrDefault("cloud_provider_name") ?? string.Empty;
        var rootPath = metadata.GetValueOrDefault("cloud_provider_root_path") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(providerId) ||
            string.IsNullOrWhiteSpace(providerName) ||
            string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        return new CloudProviderMetadata(
            providerId,
            providerName,
            metadata.GetValueOrDefault("cloud_provider_account_label") ?? string.Empty,
            rootPath);
    }

    // Build the connection through SqliteConnectionStringBuilder rather than string
    // interpolation: a chosen file path may legally contain ';', which would otherwise be
    // parsed as extra connection-string keywords (e.g. overriding Mode). The builder quotes
    // the DataSource so the path is always treated as a literal value.
    private static SqliteConnection CreateConnection(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false
        };

        return new SqliteConnection(builder.ConnectionString);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task ApplySavePragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
        => ExecuteAsync(connection, """
            PRAGMA temp_store=MEMORY;
            """, cancellationToken);

    private static async Task InsertMetadataAsync(SqliteConnection connection, string key, string value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO metadata (key, value) VALUES ($key, $value)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand CreateInsertEntryCommand(
        SqliteConnection connection,
        DbTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO entries
            (id, parent_id, name, full_path, is_directory, logical_size_bytes, allocated_size_bytes, file_count, directory_count, extension, attributes, last_write_utc, portable_object_id, portable_persistent_id)
            VALUES ($id, $parent_id, $name, $full_path, $is_directory, $logical_size_bytes, $allocated_size_bytes, $file_count, $directory_count, $extension, $attributes, $last_write_utc, $portable_object_id, $portable_persistent_id)
            """;
        AddParameter(command, "$id", SqliteType.Integer);
        AddParameter(command, "$parent_id", SqliteType.Integer);
        AddParameter(command, "$name", SqliteType.Text);
        AddParameter(command, "$full_path", SqliteType.Text);
        AddParameter(command, "$is_directory", SqliteType.Integer);
        AddParameter(command, "$logical_size_bytes", SqliteType.Integer);
        AddParameter(command, "$allocated_size_bytes", SqliteType.Integer);
        AddParameter(command, "$file_count", SqliteType.Integer);
        AddParameter(command, "$directory_count", SqliteType.Integer);
        AddParameter(command, "$extension", SqliteType.Text);
        AddParameter(command, "$attributes", SqliteType.Text);
        AddParameter(command, "$last_write_utc", SqliteType.Text);
        AddParameter(command, "$portable_object_id", SqliteType.Text);
        AddParameter(command, "$portable_persistent_id", SqliteType.Text);
        return command;
    }

    private static void AddParameter(SqliteCommand command, string name, SqliteType type)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.SqliteType = type;
        command.Parameters.Add(parameter);
    }

    // Ids are assigned in pre-order (an entry, then each child's subtree in order) so that loading
    // ORDER BY id restores every Children list in its saved order. The walk is iterative so a very
    // deep tree cannot overflow the stack.
    private static async Task InsertEntriesAsync(
        SqliteCommand command,
        FileSystemEntry root,
        CancellationToken cancellationToken)
    {
        var nextId = 1L;
        var pending = new Stack<(FileSystemEntry Entry, long? ParentId)>();
        pending.Push((root, null));
        while (pending.Count > 0)
        {
            var (entry, parentId) = pending.Pop();
            var id = nextId++;
            await InsertEntryAsync(command, entry, id, parentId, cancellationToken).ConfigureAwait(false);

            var children = entry.Children;
            for (var i = children.Count - 1; i >= 0; i--)
            {
                pending.Push((children[i], id));
            }
        }
    }

    private static async Task InsertEntryAsync(
        SqliteCommand command,
        FileSystemEntry entry,
        long id,
        long? parentId,
        CancellationToken cancellationToken)
    {
        command.Parameters[0].Value = id;
        command.Parameters[1].Value = parentId is null ? DBNull.Value : parentId.Value;
        command.Parameters[2].Value = entry.Name;
        command.Parameters[3].Value = entry.FullPath;
        command.Parameters[4].Value = entry.IsDirectory ? 1 : 0;
        command.Parameters[5].Value = entry.LogicalSizeBytes;
        command.Parameters[6].Value = entry.AllocatedSizeBytes;
        command.Parameters[7].Value = entry.FileCount;
        command.Parameters[8].Value = entry.DirectoryCount;
        command.Parameters[9].Value = entry.Extension;
        command.Parameters[10].Value = entry.Attributes;
        command.Parameters[11].Value = entry.LastWriteTime?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value;
        command.Parameters[12].Value = entry.PortableObjectId;
        command.Parameters[13].Value = entry.PortablePersistentId;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertSkippedAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        SkippedEntry skipped,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT INTO skipped_entries (path, reason) VALUES ($path, $reason)";
        command.Parameters.AddWithValue("$path", skipped.Path);
        command.Parameters.AddWithValue("$reason", skipped.Reason);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, string>> LoadMetadataAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value, typeof(key) = 'text' AND typeof(value) = 'text' FROM metadata";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt64(2) == 0)
            {
                throw new InvalidDataException("Scan file metadata contains a missing or wrongly typed value.");
            }

            metadata[reader.GetString(0)] = reader.GetString(1);
        }

        return metadata;
    }

    // These checks use PRAGMA statements, not the pragma_* table-valued functions: a scan file can
    // define its own view named pragma_table_list (or pragma_table_xinfo), and in a SELECT that
    // view would shadow the built-in function and report whatever the file wants.
    private static async Task EnsureOrdinaryTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var tableTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA main.table_list";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var nameOrdinal = reader.GetOrdinal("name");
            var typeOrdinal = reader.GetOrdinal("type");
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tableTypes[reader.GetString(nameOrdinal)] = reader.GetString(typeOrdinal);
            }
        }

        foreach (var table in LoadedTables)
        {
            if (!tableTypes.TryGetValue(table, out var type))
            {
                throw new InvalidDataException($"Scan file is missing the '{table}' table.");
            }

            if (!string.Equals(type, "table", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Scan file '{table}' is a {type}, not an ordinary table.");
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA main.table_xinfo({table})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var columnNameOrdinal = reader.GetOrdinal("name");
            var hiddenOrdinal = reader.GetOrdinal("hidden");
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetInt64(hiddenOrdinal) != 0)
                {
                    throw new InvalidDataException(
                        $"Scan file '{table}' table has a generated column '{reader.GetString(columnNameOrdinal)}'.");
                }
            }
        }
    }

    private static async Task<HashSet<string>> LoadTableColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
