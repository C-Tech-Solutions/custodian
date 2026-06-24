using Custodian.Core.Model;
using Custodian.Core.Analysis;
using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Custodian.Core.Storage;

public sealed class ScanStore
{
    public async Task SaveAsync(ScanResult result, string path, CancellationToken cancellationToken = default)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        // Pooling is disabled so the file handle is released as soon as the connection is
        // disposed. With the default pool the handle lingers, causing the File.Delete above
        // (or an external rename/overwrite) to fail with a sharing violation.
        await using var connection = CreateConnection(path, SqliteOpenMode.ReadWriteCreate);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

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
        await InsertMetadataAsync(connection, "format", "custodian-scan-v1", cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "root_path", result.RootPath, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "source_kind", result.SourceKind.ToString(), cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "source_id", sourceId, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "display_root_path", displayRootPath, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "portable_device_id", result.PortableDeviceId, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "portable_storage_object_id", result.PortableStorageObjectId, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "portable_device_name", result.PortableDeviceName, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "portable_storage_name", result.PortableStorageName, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "engine", result.Engine, cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "started_at", result.StartedAt.ToString("O"), cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(connection, "completed_at", result.CompletedAt.ToString("O"), cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await InsertEntryAsync(connection, transaction, result.Root, null, cancellationToken).ConfigureAwait(false);

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

        var metadata = await LoadMetadataAsync(connection, cancellationToken).ConfigureAwait(false);
        var entryColumns = await LoadTableColumnsAsync(connection, "entries", cancellationToken).ConfigureAwait(false);
        var portableObjectIdSelect = entryColumns.Contains("portable_object_id") ? "portable_object_id" : "''";
        var portablePersistentIdSelect = entryColumns.Contains("portable_persistent_id") ? "portable_persistent_id" : "''";
        var byId = new Dictionary<long, FileSystemEntry>();
        var parentById = new Dictionary<long, long?>();
        var indexBuilder = new ScanGlobalIndexBuilder();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT id, parent_id, name, full_path, is_directory, logical_size_bytes, allocated_size_bytes, file_count, directory_count, extension, attributes, last_write_utc, {portableObjectIdSelect}, {portablePersistentIdSelect}
                FROM entries
                ORDER BY id
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt64(0);
                long? parentId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                var entry = new FileSystemEntry
                {
                    Name = reader.GetString(2),
                    FullPath = reader.GetString(3),
                    IsDirectory = reader.GetInt32(4) != 0,
                    LogicalSizeBytes = reader.GetInt64(5),
                    AllocatedSizeBytes = reader.GetInt64(6),
                    FileCount = reader.GetInt64(7),
                    DirectoryCount = reader.GetInt64(8),
                    Extension = reader.GetString(9),
                    Attributes = reader.GetString(10),
                    LastWriteTime = reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
                    PortableObjectId = reader.GetString(12),
                    PortablePersistentId = reader.GetString(13)
                };
                byId[id] = entry;
                parentById[id] = parentId;
                indexBuilder.Observe(entry);
            }
        }

        foreach (var (id, entry) in byId)
        {
            if (parentById[id] is { } parentId && byId.TryGetValue(parentId, out var parent))
            {
                parent.Children.Add(entry);
            }
        }

        var rootIds = parentById.Where(pair => pair.Value is null).Select(pair => pair.Key).ToList();
        if (rootIds.Count != 1)
        {
            throw new InvalidDataException(rootIds.Count == 0
                ? "No root folder found in scan file."
                : "Multiple root folders found in scan file.");
        }

        if (!byId.TryGetValue(rootIds[0], out var root))
        {
            throw new InvalidDataException("Root folder ID not found in scan file entries.");
        }

        var rootPath = metadata["root_path"];
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
            Engine = metadata["engine"],
            StartedAt = DateTimeOffset.Parse(metadata["started_at"]),
            CompletedAt = DateTimeOffset.Parse(metadata["completed_at"]),
            Root = root,
            GlobalIndex = indexBuilder.Build(root)
        };

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT path, reason FROM skipped_entries ORDER BY id";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.SkippedEntries.Add(new SkippedEntry(reader.GetString(0), reader.GetString(1)));
            }
        }

        return result;
    }

    private static ScanSourceKind ParseSourceKind(string? value)
    {
        return Enum.TryParse<ScanSourceKind>(value, ignoreCase: true, out var sourceKind)
            ? sourceKind
            : ScanSourceKind.FileSystem;
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

    private static async Task InsertMetadataAsync(SqliteConnection connection, string key, string value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO metadata (key, value) VALUES ($key, $value)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> InsertEntryAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        FileSystemEntry entry,
        long? parentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO entries
            (parent_id, name, full_path, is_directory, logical_size_bytes, allocated_size_bytes, file_count, directory_count, extension, attributes, last_write_utc, portable_object_id, portable_persistent_id)
            VALUES ($parent_id, $name, $full_path, $is_directory, $logical_size_bytes, $allocated_size_bytes, $file_count, $directory_count, $extension, $attributes, $last_write_utc, $portable_object_id, $portable_persistent_id);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$parent_id", parentId is null ? DBNull.Value : parentId.Value);
        command.Parameters.AddWithValue("$name", entry.Name);
        command.Parameters.AddWithValue("$full_path", entry.FullPath);
        command.Parameters.AddWithValue("$is_directory", entry.IsDirectory ? 1 : 0);
        command.Parameters.AddWithValue("$logical_size_bytes", entry.LogicalSizeBytes);
        command.Parameters.AddWithValue("$allocated_size_bytes", entry.AllocatedSizeBytes);
        command.Parameters.AddWithValue("$file_count", entry.FileCount);
        command.Parameters.AddWithValue("$directory_count", entry.DirectoryCount);
        command.Parameters.AddWithValue("$extension", entry.Extension);
        command.Parameters.AddWithValue("$attributes", entry.Attributes);
        command.Parameters.AddWithValue("$last_write_utc", entry.LastWriteTime?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$portable_object_id", entry.PortableObjectId);
        command.Parameters.AddWithValue("$portable_persistent_id", entry.PortablePersistentId);
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);

        foreach (var child in entry.Children)
        {
            await InsertEntryAsync(connection, transaction, child, id, cancellationToken).ConfigureAwait(false);
        }

        return id;
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
        command.CommandText = "SELECT key, value FROM metadata";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            metadata[reader.GetString(0)] = reader.GetString(1);
        }

        return metadata;
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
