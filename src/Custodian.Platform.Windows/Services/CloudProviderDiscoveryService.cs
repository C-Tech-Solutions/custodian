using Custodian.Core.Model;
using Microsoft.Win32;

namespace Custodian.Platform.Windows.Services;

internal sealed class CloudProviderDiscoveryService
{
    private const string OneDriveProviderId = "onedrive";
    private const string OneDriveProviderName = "OneDrive";
    private readonly ICloudProviderDiscoveryEnvironment _environment;

    public CloudProviderDiscoveryService()
        : this(WindowsCloudProviderDiscoveryEnvironment.Instance)
    {
    }

    internal CloudProviderDiscoveryService(ICloudProviderDiscoveryEnvironment environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public Task<IReadOnlyList<CloudProviderTarget>> GetTargetsAsync(CancellationToken cancellationToken = default)
        => Task.Run(GetTargets, cancellationToken);

    public IReadOnlyList<CloudProviderTarget> GetTargets()
    {
        var knownFolders = _environment.GetKnownFolderPaths();
        var targets = new Dictionary<string, CloudProviderTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in GetOneDriveCandidates())
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
                OneDriveProviderId,
                OneDriveProviderName,
                candidate.AccountLabel,
                normalizedRoot,
                BuildDetailText(candidate.AccountLabel, normalizedRoot, knownFolderNames),
                knownFolderNames);

            if (!targets.TryGetValue(normalizedRoot, out var existing) ||
                (string.IsNullOrWhiteSpace(existing.AccountLabel) && !string.IsNullOrWhiteSpace(target.AccountLabel)))
            {
                targets[normalizedRoot] = target;
            }
        }

        return targets.Values
            .OrderBy(target => target.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.AccountLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public CloudProviderMetadata? TryMatchPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !TryNormalizeRoot(path, out var normalizedPath))
        {
            return null;
        }

        return GetTargets()
            .Where(target => IsPathWithinRoot(normalizedPath, target.RootPath))
            .OrderByDescending(target => target.RootPath.Length)
            .FirstOrDefault()
            ?.ToMetadata();
    }

    private IEnumerable<OneDriveRootCandidate> GetOneDriveCandidates()
    {
        foreach (var candidate in _environment.GetOneDriveAccountCandidates())
        {
            yield return candidate;
        }

        foreach (var candidate in _environment.GetOneDriveEnvironmentCandidates())
        {
            yield return candidate;
        }

        var profile = _environment.GetUserProfilePath();
        if (!string.IsNullOrWhiteSpace(profile))
        {
            yield return new OneDriveRootCandidate(Path.Combine(profile, "OneDrive"), "Personal");
        }
    }

    private static string BuildDetailText(string accountLabel, string rootPath, IReadOnlyCollection<string> knownFolderNames)
    {
        var account = string.IsNullOrWhiteSpace(accountLabel) ? OneDriveProviderName : accountLabel;
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

        try
        {
            normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
            normalized = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(normalized);
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            if (string.Equals(normalized, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                normalized = root;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            normalized = string.Empty;
            return false;
        }
    }
}

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

internal interface ICloudProviderDiscoveryEnvironment
{
    IEnumerable<OneDriveRootCandidate> GetOneDriveAccountCandidates();
    IEnumerable<OneDriveRootCandidate> GetOneDriveEnvironmentCandidates();
    IReadOnlyDictionary<string, string> GetKnownFolderPaths();
    string? GetUserProfilePath();
    bool DirectoryExists(string path);
}

internal sealed class WindowsCloudProviderDiscoveryEnvironment : ICloudProviderDiscoveryEnvironment
{
    public static WindowsCloudProviderDiscoveryEnvironment Instance { get; } = new();

    private WindowsCloudProviderDiscoveryEnvironment()
    {
    }

    public IEnumerable<OneDriveRootCandidate> GetOneDriveAccountCandidates()
    {
        using var accounts = OpenOneDriveAccountsKey();
        if (accounts is null)
        {
            yield break;
        }

        foreach (var subkeyName in GetSubKeyNames(accounts))
        {
            using var account = OpenSubKey(accounts, subkeyName);
            var rootPath = account?.GetValue("UserFolder") as string;
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                continue;
            }

            var displayName = account?.GetValue("DisplayName") as string;
            yield return new OneDriveRootCandidate(rootPath, BuildAccountLabel(subkeyName, displayName));
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

    private static RegistryKey? OpenOneDriveAccountsKey()
    {
        try
        {
            return Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
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
