using Custodian.Core.Model;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Custodian.Core.Storage;

public sealed class ScanStore
{
    private readonly ScanLoadLimits _loadLimits;

    public ScanStore() : this(new ScanLoadLimits())
    {
    }

    public ScanStore(ScanLoadLimits loadLimits)
    {
        ArgumentNullException.ThrowIfNull(loadLimits);
        loadLimits.Validate();
        _loadLimits = loadLimits;
    }

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
        await InsertMetadataAsync(connection, "format", "custodian-scan-v1", cancellationToken).ConfigureAwait(false);
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
        await InsertEntryAsync(entryCommand, result.Root, null, nextId: 1, cancellationToken).ConfigureAwait(false);

        foreach (var skipped in result.SkippedEntries)
        {
            await InsertSkippedAsync(connection, transaction, skipped, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScanResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Keep the file open while reading so Windows cannot replace or grow it
        // between the size check and SQLite's read transaction.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = file.Length;
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = new FileInfo(path + suffix);
            if (sidecar.Exists)
            {
                bytes = checked(bytes + sidecar.Length);
            }
        }

        if (bytes > _loadLimits.MaxFileBytes)
        {
            throw new InvalidDataException("Saved scan exceeds the configured file byte limit.");
        }

        await using var connection = CreateConnection(path, SqliteOpenMode.ReadOnly);
        return await new SavedScanReader(connection, _loadLimits, cancellationToken).ReadAsync().ConfigureAwait(false);
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

    private static async Task<long> InsertEntryAsync(
        SqliteCommand command,
        FileSystemEntry entry,
        long? parentId,
        long nextId,
        CancellationToken cancellationToken)
    {
        var id = nextId;
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
        nextId++;

        foreach (var child in entry.Children)
        {
            nextId = await InsertEntryAsync(command, child, id, nextId, cancellationToken).ConfigureAwait(false);
        }

        return nextId;
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

}
