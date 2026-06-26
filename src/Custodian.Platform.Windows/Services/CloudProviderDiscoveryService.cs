using Custodian.Core.Model;
using Custodian.Platform.Windows.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Text.Json;

namespace Custodian.Platform.Windows.Services;

internal sealed class CloudProviderDiscoveryService
{
    private const string OneDriveProviderId = "onedrive";
    private const string OneDriveProviderName = "OneDrive";
    private const string NextcloudProviderId = "nextcloud";
    private const string NextcloudProviderName = "Nextcloud";
    private const string DropboxProviderId = "dropbox";
    private const string DropboxProviderName = "Dropbox";
    private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(CloudProviderDiscoveryService).FullName!);
    private readonly ICloudProviderDiscoveryEnvironment _environment;
    private readonly object _lock = new();
    private IReadOnlyList<CloudProviderTarget>? _cachedTargets;

    public CloudProviderDiscoveryService()
        : this(WindowsCloudProviderDiscoveryEnvironment.Instance)
    {
    }

    internal CloudProviderDiscoveryService(ICloudProviderDiscoveryEnvironment environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public Task<IReadOnlyList<CloudProviderTarget>> GetTargetsAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => GetTargets(forceRefresh: true), cancellationToken);

    public IReadOnlyList<CloudProviderTarget> GetTargets(bool forceRefresh = false)
    {
        if (!_environment.IsSupported)
        {
            return [];
        }

        lock (_lock)
        {
            if (!forceRefresh && _cachedTargets is not null)
            {
                return _cachedTargets;
            }

            try
            {
                var targets = DiscoverTargets();
                _cachedTargets = targets;
                return targets;
            }
            catch (Exception ex) when (IsRecoverableDiscoveryException(ex))
            {
                Logger.LogDebug(ex, "Cloud provider discovery failed; returning cached targets if available.");
                return _cachedTargets ?? [];
            }
        }
    }

    private IReadOnlyList<CloudProviderTarget> DiscoverTargets()
    {
        var knownFolders = _environment.GetKnownFolderPaths();
        var targets = new Dictionary<string, CloudProviderTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in GetCloudProviderCandidates())
        {
            if (!TryNormalizeRoot(candidate.RootPath, out var normalizedRoot) ||
                !_environment.DirectoryExists(normalizedRoot))
            {
                continue;
            }

            var knownFolderNames = knownFolders
                .Where(pair => IsPathWithinRoot(pair.Value, normalizedRoot))
                .Select(pair => pair.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var target = new CloudProviderTarget(
                candidate.ProviderId,
                candidate.ProviderName,
                candidate.AccountLabel,
                normalizedRoot,
                BuildDetailText(candidate.ProviderName, candidate.AccountLabel, normalizedRoot, knownFolderNames),
                knownFolderNames);
            var targetKey = $"{target.ProviderId}\0{normalizedRoot}";

            if (!targets.TryGetValue(targetKey, out var existing) ||
                (string.IsNullOrWhiteSpace(existing.AccountLabel) && !string.IsNullOrWhiteSpace(target.AccountLabel)))
            {
                targets[targetKey] = target;
            }
        }

        return targets.Values
            .OrderBy(target => target.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.AccountLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsRecoverableDiscoveryException(Exception ex)
        => ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or System.Security.SecurityException;

    public CloudProviderMetadata? TryMatchPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !TryNormalizeRoot(path, out var normalizedPath))
        {
            return null;
        }

        return GetTargets()
            .Where(target => IsNormalizedPathWithinRoot(normalizedPath, target.RootPath))
            .OrderByDescending(target => target.RootPath.Length)
            .FirstOrDefault()
            ?.ToMetadata();
    }

    private IEnumerable<CloudProviderRootCandidate> GetCloudProviderCandidates()
    {
        foreach (var candidate in GetOneDriveCandidates())
        {
            yield return candidate;
        }

        foreach (var candidate in GetNextcloudCandidates())
        {
            yield return candidate;
        }

        foreach (var candidate in GetDropboxCandidates())
        {
            yield return candidate;
        }
    }

    private IEnumerable<CloudProviderRootCandidate> GetOneDriveCandidates()
    {
        foreach (var candidate in _environment.GetOneDriveAccountCandidates())
        {
            yield return ToOneDriveCandidate(candidate);
        }

        foreach (var candidate in _environment.GetOneDriveEnvironmentCandidates())
        {
            yield return ToOneDriveCandidate(candidate);
        }

        var profile = _environment.GetUserProfilePath();
        if (!string.IsNullOrWhiteSpace(profile))
        {
            yield return ToOneDriveCandidate(new OneDriveRootCandidate(Path.Combine(profile, "OneDrive"), "Personal"));
        }
    }

    private static CloudProviderRootCandidate ToOneDriveCandidate(OneDriveRootCandidate candidate)
        => new(OneDriveProviderId, OneDriveProviderName, candidate.RootPath, candidate.AccountLabel);

    private IEnumerable<CloudProviderRootCandidate> GetNextcloudCandidates()
    {
        var configuredCandidates = _environment.GetNextcloudConfigurationFiles()
            .SelectMany(configFile => ParseNextcloudConfiguration(configFile.Content))
            .Where(IsExistingCloudProviderRoot)
            .ToArray();

        if (configuredCandidates.Length > 0)
        {
            foreach (var candidate in configuredCandidates)
            {
                yield return candidate;
            }

            yield break;
        }

        foreach (var rootPath in _environment.GetNextcloudProfileCandidates())
        {
            yield return new CloudProviderRootCandidate(NextcloudProviderId, NextcloudProviderName, rootPath, string.Empty);
        }
    }

    private bool IsExistingCloudProviderRoot(CloudProviderRootCandidate candidate)
        => TryNormalizeRoot(candidate.RootPath, out var normalizedRoot) &&
            _environment.DirectoryExists(normalizedRoot);

    private IEnumerable<CloudProviderRootCandidate> GetDropboxCandidates()
    {
        var configuredCandidates = _environment.GetDropboxConfigurationFiles()
            .SelectMany(configFile => ParseDropboxConfiguration(configFile.Content))
            .Where(IsExistingCloudProviderRoot)
            .ToArray();

        if (configuredCandidates.Length > 0)
        {
            foreach (var candidate in configuredCandidates)
            {
                yield return candidate;
            }

            yield break;
        }

        foreach (var rootPath in _environment.GetDropboxProfileCandidates())
        {
            yield return new CloudProviderRootCandidate(DropboxProviderId, DropboxProviderName, rootPath, string.Empty);
        }
    }

    internal static IReadOnlyList<CloudProviderRootCandidate> ParseNextcloudConfiguration(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var accountLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var syncRoots = new List<(string AccountKey, string RootPath)>();
        var section = string.Empty;

        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                section = line[1..^1].Trim();
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = TrimConfigurationValue(line[(separatorIndex + 1)..]);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var qualifiedKey = string.IsNullOrWhiteSpace(section)
                ? key
                : $"{section}\\{key}";
            var parts = qualifiedKey
                .Replace('/', '\\')
                .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 3 &&
                parts[0].Equals("Accounts", StringComparison.OrdinalIgnoreCase) &&
                parts[2].Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                var label = BuildNextcloudAccountLabel(value);
                if (!string.IsNullOrWhiteSpace(label))
                {
                    accountLabels[parts[1]] = label;
                }

                continue;
            }

            if (parts.Length >= 5 &&
                parts[0].Equals("Accounts", StringComparison.OrdinalIgnoreCase) &&
                (parts[2].Equals("FoldersWithPlaceholders", StringComparison.OrdinalIgnoreCase) ||
                 parts[2].Equals("Folders", StringComparison.OrdinalIgnoreCase)) &&
                parts[^1].Equals("localPath", StringComparison.OrdinalIgnoreCase))
            {
                syncRoots.Add((parts[1], value));
            }
        }

        return syncRoots
            .Select(root => new CloudProviderRootCandidate(
                NextcloudProviderId,
                NextcloudProviderName,
                root.RootPath,
                accountLabels.GetValueOrDefault(root.AccountKey, string.Empty)))
            .ToArray();
    }

    internal static IReadOnlyList<CloudProviderRootCandidate> ParseDropboxConfiguration(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var candidates = new List<CloudProviderRootCandidate>();
            AddDropboxAccountCandidate(document.RootElement, "personal", "Personal", candidates);
            AddDropboxAccountCandidate(document.RootElement, "business", "Business", candidates);
            return candidates;
        }
        catch (JsonException ex)
        {
            Logger.LogDebug(ex, "Unable to parse Dropbox configuration.");
            return [];
        }
    }

    private static void AddDropboxAccountCandidate(
        JsonElement root,
        string propertyName,
        string accountLabel,
        ICollection<CloudProviderRootCandidate> candidates)
    {
        if (!root.TryGetProperty(propertyName, out var account) ||
            account.ValueKind != JsonValueKind.Object ||
            !account.TryGetProperty("path", out var pathProperty) ||
            pathProperty.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var rootPath = pathProperty.GetString();
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        candidates.Add(new CloudProviderRootCandidate(
            DropboxProviderId,
            DropboxProviderName,
            rootPath,
            accountLabel));
    }

    private static string TrimConfigurationValue(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 &&
               ((trimmed[0] == '"' && trimmed[^1] == '"') ||
                (trimmed[0] == '\'' && trimmed[^1] == '\''))
            ? trimmed[1..^1].Trim()
            : trimmed;
    }

    private static string BuildNextcloudAccountLabel(string value)
    {
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return string.Empty;
    }

    private static string BuildDetailText(string providerName, string accountLabel, string rootPath, IReadOnlyCollection<string> knownFolderNames)
    {
        var account = string.IsNullOrWhiteSpace(accountLabel) ? providerName : accountLabel;
        if (knownFolderNames.Count == 0)
        {
            return $"{account} - {rootPath}";
        }

        return $"{account} - {rootPath} - includes {string.Join(", ", knownFolderNames)}";
    }

    internal static bool IsPathWithinRoot(string path, string rootPath)
    {
        if (!TryNormalizeRoot(path, out var normalizedPath) ||
            !TryNormalizeRoot(rootPath, out var normalizedRoot))
        {
            return false;
        }

        return IsNormalizedPathWithinRoot(normalizedPath, normalizedRoot);
    }

    internal static bool IsNormalizedPathWithinRoot(string normalizedPath, string normalizedRoot)
    {
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.Length > normalizedRoot.Length &&
            normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            (normalizedRoot.EndsWith(Path.DirectorySeparatorChar) ||
             normalizedPath[normalizedRoot.Length] == Path.DirectorySeparatorChar);
    }

    internal static bool TryNormalizeRoot(string path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();
        if (IsIncompleteUncPath(trimmed))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(trimmed));
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalized, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                normalized = root;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            Logger.LogDebug(ex, "Unable to normalize cloud provider path {Path}.", path);
            normalized = string.Empty;
            return false;
        }
    }

    private static bool IsIncompleteUncPath(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal) &&
            !path.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        var separatorIndex = path.IndexOfAny(['\\', '/'], 2);
        return separatorIndex < 0 || separatorIndex == path.Length - 1;
    }
}

internal sealed record CloudProviderRootCandidate(
    string ProviderId,
    string ProviderName,
    string RootPath,
    string AccountLabel);

internal sealed record CloudProviderTarget(
    string ProviderId,
    string ProviderName,
    string AccountLabel,
    string RootPath,
    string DetailText,
    IReadOnlyList<string> KnownFolderNames)
{
    public CloudProviderMetadata ToMetadata()
        => new(ProviderId, ProviderName, AccountLabel, RootPath);
}

internal sealed record OneDriveRootCandidate(string RootPath, string AccountLabel);

internal sealed record NextcloudConfigurationFile(string FilePath, string Content);

internal sealed record DropboxConfigurationFile(string FilePath, string Content);

internal interface ICloudProviderDiscoveryEnvironment
{
    bool IsSupported { get; }
    IEnumerable<OneDriveRootCandidate> GetOneDriveAccountCandidates();
    IEnumerable<OneDriveRootCandidate> GetOneDriveEnvironmentCandidates();
    IEnumerable<NextcloudConfigurationFile> GetNextcloudConfigurationFiles();
    IEnumerable<string> GetNextcloudProfileCandidates();
    IEnumerable<DropboxConfigurationFile> GetDropboxConfigurationFiles();
    IEnumerable<string> GetDropboxProfileCandidates();
    IReadOnlyDictionary<string, string> GetKnownFolderPaths();
    string? GetUserProfilePath();
    bool DirectoryExists(string path);
}

internal sealed class WindowsCloudProviderDiscoveryEnvironment : ICloudProviderDiscoveryEnvironment
{
    public static WindowsCloudProviderDiscoveryEnvironment Instance { get; } = new();
    private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(WindowsCloudProviderDiscoveryEnvironment).FullName!);

    private WindowsCloudProviderDiscoveryEnvironment()
    {
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public IEnumerable<OneDriveRootCandidate> GetOneDriveAccountCandidates()
    {
        using var accounts = OpenOneDriveAccountsKey();
        if (accounts is null)
        {
            yield break;
        }

        foreach (var subkeyName in GetSubKeyNames(accounts))
        {
            var candidate = ReadAccountCandidate(accounts, subkeyName);
            if (candidate is not null)
            {
                yield return candidate;
            }
        }
    }

    public IEnumerable<OneDriveRootCandidate> GetOneDriveEnvironmentCandidates()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key ||
                entry.Value is not string value ||
                !key.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            yield return new OneDriveRootCandidate(value, BuildEnvironmentAccountLabel(key));
        }
    }

    public IEnumerable<NextcloudConfigurationFile> GetNextcloudConfigurationFiles()
    {
        foreach (var path in GetNextcloudConfigurationFilePaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var content = ReadConfigurationFile(path);
            if (content is not null)
            {
                yield return new NextcloudConfigurationFile(path, content);
            }
        }
    }

    public IEnumerable<DropboxConfigurationFile> GetDropboxConfigurationFiles()
    {
        foreach (var path in GetDropboxConfigurationFilePaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var content = ReadConfigurationFile(path);
            if (content is not null)
            {
                yield return new DropboxConfigurationFile(path, content);
            }
        }
    }

    public IEnumerable<string> GetNextcloudProfileCandidates()
    {
        var profile = GetUserProfilePath();
        if (string.IsNullOrWhiteSpace(profile))
        {
            yield break;
        }

        yield return Path.Combine(profile, "Nextcloud");

        foreach (var directory in EnumerateDirectories(profile, "Nextcloud*"))
        {
            yield return directory;
        }
    }

    public IEnumerable<string> GetDropboxProfileCandidates()
    {
        var profile = GetUserProfilePath();
        if (string.IsNullOrWhiteSpace(profile))
        {
            yield break;
        }

        yield return Path.Combine(profile, "Dropbox");

        foreach (var directory in EnumerateDirectories(profile, "Dropbox*"))
        {
            yield return directory;
        }
    }

    public IReadOnlyDictionary<string, string> GetKnownFolderPaths()
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddKnownFolder(paths, "Desktop", Environment.SpecialFolder.DesktopDirectory);
        AddKnownFolder(paths, "Documents", Environment.SpecialFolder.MyDocuments);
        AddKnownFolder(paths, "Pictures", Environment.SpecialFolder.MyPictures);
        return paths;
    }

    public string? GetUserProfilePath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? null : profile;
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    private static IEnumerable<string> GetNextcloudConfigurationFilePaths()
    {
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roamingAppData))
        {
            yield return Path.Combine(roamingAppData, "Nextcloud", "nextcloud.cfg");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Nextcloud", "nextcloud.cfg");
        }
    }

    private static IEnumerable<string> GetDropboxConfigurationFilePaths()
    {
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roamingAppData))
        {
            yield return Path.Combine(roamingAppData, "Dropbox", "info.json");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Dropbox", "info.json");
        }
    }

    private static string? ReadConfigurationFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            Logger.LogDebug(ex, "Unable to read cloud provider configuration file {Path}.", path);
            return null;
        }
    }

    private static IReadOnlyList<string> EnumerateDirectories(string path, string searchPattern)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateDirectories(path, searchPattern, SearchOption.TopDirectoryOnly).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            Logger.LogDebug(ex, "Unable to enumerate cloud provider profile candidate directories under {Path}.", path);
            return [];
        }
    }

    private static RegistryKey? OpenOneDriveAccountsKey()
    {
        try
        {
            return Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Logger.LogDebug(ex, "Unable to open the OneDrive accounts registry key.");
            return null;
        }
    }

    private static RegistryKey? OpenSubKey(RegistryKey key, string subkeyName)
    {
        try
        {
            return key.OpenSubKey(subkeyName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Logger.LogDebug(ex, "Unable to open OneDrive account registry subkey {SubkeyName}.", subkeyName);
            return null;
        }
    }

    private static OneDriveRootCandidate? ReadAccountCandidate(RegistryKey accounts, string subkeyName)
    {
        try
        {
            using var account = OpenSubKey(accounts, subkeyName);
            var rootPath = account?.GetValue("UserFolder") as string;
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return null;
            }

            var displayName = account?.GetValue("DisplayName") as string;
            return new OneDriveRootCandidate(rootPath, BuildAccountLabel(subkeyName, displayName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Logger.LogDebug(ex, "Unable to read OneDrive account registry subkey {SubkeyName}.", subkeyName);
            return null;
        }
    }

    private static IReadOnlyList<string> GetSubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Logger.LogDebug(ex, "Unable to enumerate OneDrive account registry subkeys.");
            return [];
        }
    }

    private static void AddKnownFolder(IDictionary<string, string> paths, string name, Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (!string.IsNullOrWhiteSpace(path))
        {
            paths[name] = path;
        }
    }

    private static string BuildAccountLabel(string subkeyName, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName.Trim();
        }

        if (subkeyName.Contains("Personal", StringComparison.OrdinalIgnoreCase))
        {
            return "Personal";
        }

        if (subkeyName.Contains("Business", StringComparison.OrdinalIgnoreCase))
        {
            return "Business";
        }

        return subkeyName;
    }

    private static string BuildEnvironmentAccountLabel(string key)
    {
        if (key.Contains("Commercial", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Business", StringComparison.OrdinalIgnoreCase))
        {
            return "Business";
        }

        return "Personal";
    }
}
