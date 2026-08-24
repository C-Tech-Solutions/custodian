using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Velopack;
using Velopack.Locators;

namespace Custodian.Platform.Windows.Services;

internal interface IUpdatePackageVerifier
{
    void Verify(VelopackAsset asset);
}

internal sealed class VelopackUpdatePackageVerifier : IUpdatePackageVerifier
{
    private readonly IUpdatePackagePathResolver _packagePathResolver;
    private readonly IUpdatePackageChecksumVerifier _checksumVerifier;
    private readonly IAuthenticodeSignatureVerifier _signatureVerifier;

    public VelopackUpdatePackageVerifier(
        IVelopackLocator? locator,
        IAuthenticodeSignatureVerifier signatureVerifier)
        : this(
            new VelopackUpdatePackagePathResolver(locator),
            new VelopackUpdatePackageChecksumVerifier(),
            signatureVerifier)
    {
    }

    internal VelopackUpdatePackageVerifier(
        IUpdatePackagePathResolver packagePathResolver,
        IUpdatePackageChecksumVerifier checksumVerifier,
        IAuthenticodeSignatureVerifier signatureVerifier)
    {
        _packagePathResolver = packagePathResolver;
        _checksumVerifier = checksumVerifier;
        _signatureVerifier = signatureVerifier;
    }

    public void Verify(VelopackAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var packagePath = _packagePathResolver.GetPackagePath(asset);
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("The downloaded update package was not found.", packagePath);
        }

        _checksumVerifier.Verify(asset, packagePath);
        UpdatePackageSignatureVerifier.VerifyPackage(packagePath, _signatureVerifier);
    }
}

internal interface IUpdatePackagePathResolver
{
    string GetPackagePath(VelopackAsset asset);
}

internal sealed class VelopackUpdatePackagePathResolver(IVelopackLocator? locator) : IUpdatePackagePathResolver
{
    public string GetPackagePath(VelopackAsset asset)
    {
        if (locator is null)
        {
            throw new InvalidOperationException("Custodian could not locate the downloaded update package.");
        }

        if (string.IsNullOrWhiteSpace(locator.PackagesDir))
        {
            throw new InvalidOperationException("Custodian could not locate the Velopack packages directory.");
        }

        return Path.Combine(locator.PackagesDir, asset.FileName);
    }
}

internal interface IUpdatePackageChecksumVerifier
{
    void Verify(VelopackAsset asset, string packagePath);
}

internal sealed class VelopackUpdatePackageChecksumVerifier : IUpdatePackageChecksumVerifier
{
    public void Verify(VelopackAsset asset, string packagePath)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        if (!string.IsNullOrWhiteSpace(asset.SHA256))
        {
            VerifyHash(packagePath, asset.SHA256, SHA256.HashData, "SHA256");
            return;
        }

        if (!string.IsNullOrWhiteSpace(asset.SHA1))
        {
            VerifyHash(packagePath, asset.SHA1, SHA1.HashData, "SHA1");
            return;
        }

        throw new InvalidOperationException("The downloaded update package does not include Velopack checksum metadata.");
    }

    private static void VerifyHash(string packagePath, string expected, Func<Stream, byte[]> hashAlgorithm, string hashName)
    {
        using var stream = File.OpenRead(packagePath);
        var hashBytes = hashAlgorithm(stream);
        var actualBase64 = Convert.ToBase64String(hashBytes);
        var actualHex = Convert.ToHexString(hashBytes);

        if (string.Equals(expected, actualBase64, StringComparison.Ordinal) ||
            string.Equals(expected, actualHex, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException($"The downloaded update package failed Velopack {hashName} checksum verification.");
    }
}

internal static class UpdateSourceOverridePolicy
{
    public static string NormalizeLocalSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Update source override cannot be empty.", nameof(source));
        }

        string path;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile || uri.IsUnc)
            {
                throw new InvalidOperationException("CUSTODIAN_UPDATE_SOURCE must point to a local folder.");
            }

            path = uri.LocalPath;
        }
        else
        {
            path = source;
        }

        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CUSTODIAN_UPDATE_SOURCE must point to a local folder.");
        }

        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"CUSTODIAN_UPDATE_SOURCE does not exist: {path}");
        }

        return path;
    }
}

internal static class UpdatePackageSignatureVerifier
{
    internal const string TrustedSignerName = "C-Tech Solutions LLC";
    internal const string MicrosoftSignerOrganization = "Microsoft Corporation";
    internal const string JetBrainsSignerOrganization = "JetBrains s.r.o.";
    internal const int MaximumArchiveEntries = 4_096;
    internal const int MaximumPeEntries = 2_048;
    internal const long MaximumSingleEntryBytes = 64L * 1024 * 1024;
    internal const long MaximumArchiveUncompressedBytes = 768L * 1024 * 1024;
    internal const string JetBrainsAnnotationsPath = "lib/app/tui/JetBrains.Annotations.dll";
    private static readonly string[] PackagePeExtensions = [".dll", ".exe"];
    private static readonly IReadOnlyDictionary<string, MicrosoftFileIdentityException> MicrosoftFileIdentityExceptions =
        new Dictionary<string, MicrosoftFileIdentityException>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accessibility.dll"] = new("Accessibility-version.dll"),
            ["clrgcexp.dll"] = new("clrgc.dll"),
            ["D3DCompiler_47_cor3.dll"] = new("d3dcompiler_47.dll"),
            ["DirectWriteForwarder.dll"] = new("DirectWriteForwarder", RequireMicrosoftCompanyName: false),
            ["hostfxr.dll"] = new(".NET Host Resolver -"),
            ["hostpolicy.dll"] = new(".NET Host Policy -"),
            ["PenImc_cor3.dll"] = new("PenImc", RequireMicrosoftCompanyName: false),
            ["PresentationNative_cor3.dll"] = new("PresentationNative", RequireMicrosoftCompanyName: false),
            ["System.Diagnostics.EventLog.Messages.dll"] = new(null, RequireMicrosoftCompanyName: false),
            ["System.IO.Compression.Native.dll"] = new("System.IO.Compression.Native"),
            ["System.Printing.dll"] = new("System.Printing", RequireMicrosoftCompanyName: false),
            ["vcruntime140_cor3.dll"] = new("vcruntime140.dll"),
            ["wpfgfx_cor3.dll"] = new("wpfgfx", RequireMicrosoftCompanyName: false)
        };

    public static UpdatePackageSignatureVerificationResult VerifyPackage(
        string packagePath,
        IAuthenticodeSignatureVerifier signatureVerifier)
    {
        ArgumentNullException.ThrowIfNull(signatureVerifier);

        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("Package path is required.", nameof(packagePath));
        }

        using var archive = ZipFile.OpenRead(packagePath);
        var archiveValidation = ValidateArchiveMetadata(
            archive.Entries.Select(entry => new UpdatePackageArchiveEntryMetadata(
                entry.FullName,
                entry.Length,
                entry.CompressedLength)));
        var verifiedFiles = new List<string>();
        var verifiedCustodianFiles = 0;
        foreach (var validatedEntry in archiveValidation.Entries.Where(entry => entry.IsPeFile))
        {
            var entry = archive.Entries[validatedEntry.Index];
            var tempPath = ExtractToTemporaryFile(entry);
            try
            {
                var result = signatureVerifier.Verify(tempPath);
                if (!result.IsTrusted)
                {
                    throw new InvalidOperationException(
                        $"Update package file '{entry.FullName}' is not trusted: {result.FailureReason ?? "signature verification failed"}.");
                }

                var publisher = AuthorizePublisher(validatedEntry.NormalizedPath, result);
                if (publisher == AuthorizedPackagePublisher.None)
                {
                    throw new InvalidOperationException(
                        $"Update package file '{entry.FullName}' was signed by '{result.SignerSubject ?? result.SignerSimpleName ?? "unknown"}' but is not authorized by Custodian's update publisher policy.");
                }

                verifiedFiles.Add(entry.FullName);
                if (publisher == AuthorizedPackagePublisher.Custodian)
                {
                    verifiedCustodianFiles++;
                }
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        if (verifiedCustodianFiles == 0)
        {
            throw new InvalidOperationException("The update package did not contain any Custodian-owned executable files to verify.");
        }

        return new UpdatePackageSignatureVerificationResult(verifiedFiles);
    }

    internal static bool IsTrustedSigner(string? subject)
        => DistinguishedNameContains(subject, "O", TrustedSignerName);

    internal static UpdatePackageArchiveValidationResult ValidateArchiveMetadata(
        IEnumerable<UpdatePackageArchiveEntryMetadata> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var entries = new List<ValidatedPackageArchiveEntry>();
        var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalUncompressedBytes = 0L;
        var peCount = 0;
        var index = 0;

        foreach (var entry in metadata)
        {
            if (index >= MaximumArchiveEntries)
            {
                throw new InvalidOperationException($"The update package contains more than {MaximumArchiveEntries:n0} archive entries.");
            }

            if (entry.UncompressedLength < 0 || entry.CompressedLength < 0)
            {
                throw new InvalidOperationException($"Update package entry '{entry.FullName}' has invalid size metadata.");
            }

            var (normalizedPath, isDirectory) = NormalizeArchivePath(entry.FullName);
            if (!normalizedPaths.Add(normalizedPath))
            {
                throw new InvalidOperationException($"The update package contains a duplicate normalized path: '{entry.FullName}'.");
            }

            if (isDirectory && entry.UncompressedLength != 0)
            {
                throw new InvalidOperationException($"Update package directory entry '{entry.FullName}' contains file data.");
            }

            if (entry.UncompressedLength > MaximumSingleEntryBytes)
            {
                throw new InvalidOperationException(
                    $"Update package entry '{entry.FullName}' exceeds the {MaximumSingleEntryBytes / 1024 / 1024:n0} MiB per-entry limit.");
            }

            try
            {
                totalUncompressedBytes = checked(totalUncompressedBytes + entry.UncompressedLength);
            }
            catch (OverflowException ex)
            {
                throw new InvalidOperationException("The update package has invalid aggregate size metadata.", ex);
            }

            if (totalUncompressedBytes > MaximumArchiveUncompressedBytes)
            {
                throw new InvalidOperationException(
                    $"The update package exceeds the {MaximumArchiveUncompressedBytes / 1024 / 1024:n0} MiB total uncompressed-size limit.");
            }

            var isPeFile = !isDirectory && IsPackagePeFile(normalizedPath);
            if (isPeFile && ++peCount > MaximumPeEntries)
            {
                throw new InvalidOperationException($"The update package contains more than {MaximumPeEntries:n0} executable files.");
            }

            entries.Add(new ValidatedPackageArchiveEntry(index, normalizedPath, isPeFile));
            index++;
        }

        return new UpdatePackageArchiveValidationResult(entries, totalUncompressedBytes, peCount);
    }

    private static AuthorizedPackagePublisher AuthorizePublisher(
        string normalizedPath,
        AuthenticodeSignatureResult signature)
    {
        if (IsTrustedSigner(signature.SignerSubject))
        {
            return AuthorizedPackagePublisher.Custodian;
        }

        var fileName = GetArchiveFileName(normalizedPath);
        if (IsApprovedMicrosoftPath(normalizedPath) &&
            SignerOrganizationMatches(signature, MicrosoftSignerOrganization) &&
            MicrosoftFileIdentityMatches(fileName, signature))
        {
            return AuthorizedPackagePublisher.Microsoft;
        }

        if (string.Equals(normalizedPath, JetBrainsAnnotationsPath, StringComparison.OrdinalIgnoreCase) &&
            SignerOrganizationMatches(signature, JetBrainsSignerOrganization) &&
            string.Equals(signature.OriginalFileName, fileName, StringComparison.OrdinalIgnoreCase))
        {
            return AuthorizedPackagePublisher.JetBrains;
        }

        return AuthorizedPackagePublisher.None;
    }

    private static bool IsApprovedMicrosoftPath(string normalizedPath)
        => normalizedPath.StartsWith("lib/app/", StringComparison.OrdinalIgnoreCase);

    private static bool MicrosoftFileIdentityMatches(string fileName, AuthenticodeSignatureResult signature)
    {
        if (string.Equals(signature.CompanyName, MicrosoftSignerOrganization, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(signature.OriginalFileName, fileName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var identityException = MicrosoftFileIdentityExceptions.GetValueOrDefault(fileName);
        if (identityException is null &&
            fileName.StartsWith("mscordaccore_amd64_amd64_", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            identityException = new MicrosoftFileIdentityException("mscordaccore.dll");
        }

        return identityException is not null &&
            string.Equals(signature.OriginalFileName, identityException.OriginalFileName, StringComparison.OrdinalIgnoreCase) &&
            (!identityException.RequireMicrosoftCompanyName ||
             string.Equals(signature.CompanyName, MicrosoftSignerOrganization, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SignerOrganizationMatches(AuthenticodeSignatureResult signature, string expected)
        => string.Equals(signature.SignerOrganization, expected, StringComparison.OrdinalIgnoreCase) ||
           DistinguishedNameContains(signature.SignerSubject, "O", expected);

    private static bool IsPackagePeFile(string normalizedPath)
    {
        var extension = Path.GetExtension(GetArchiveFileName(normalizedPath));
        return PackagePeExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static (string NormalizedPath, bool IsDirectory) NormalizeArchivePath(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName) || fullName.Contains('\0'))
        {
            throw new InvalidOperationException("The update package contains an invalid empty archive path.");
        }

        var path = fullName.Replace('\\', '/');
        var isDirectory = path.EndsWith("/", StringComparison.Ordinal);
        path = path.TrimEnd('/');
        if (path.Length == 0 || path.StartsWith("/", StringComparison.Ordinal) || path.Contains(':'))
        {
            throw new InvalidOperationException($"The update package contains an unsafe archive path: '{fullName}'.");
        }

        var segments = path.Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new InvalidOperationException($"The update package contains an unsafe archive path: '{fullName}'.");
        }

        return (string.Join('/', segments), isDirectory);
    }

    private static string GetArchiveFileName(string normalizedPath)
    {
        var separator = normalizedPath.LastIndexOf('/');
        return separator >= 0 ? normalizedPath[(separator + 1)..] : normalizedPath;
    }

    private static string ExtractToTemporaryFile(ZipArchiveEntry entry)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "custodian-update-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var tempPath = Path.Combine(tempDirectory, Path.GetFileName(entry.FullName));
        try
        {
            using var input = entry.Open();
            using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[128 * 1024];
            var totalBytes = 0L;
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                totalBytes = checked(totalBytes + read);
                if (totalBytes > entry.Length || totalBytes > MaximumSingleEntryBytes)
                {
                    throw new InvalidOperationException($"Update package entry '{entry.FullName}' expanded beyond its declared size.");
                }

                output.Write(buffer, 0, read);
            }

            if (totalBytes != entry.Length)
            {
                throw new InvalidOperationException($"Update package entry '{entry.FullName}' did not match its declared size.");
            }

            return tempPath;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static bool DistinguishedNameContains(string? subject, string attributeName, string expectedValue)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        try
        {
            var distinguishedName = new X500DistinguishedName(subject);
            var decoded = distinguishedName.Decode(X500DistinguishedNameFlags.UseNewLines);
            var prefix = attributeName + "=";
            return decoded
                .Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(part[prefix.Length..].Trim(), expectedValue, StringComparison.OrdinalIgnoreCase));
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only. A temp verification file must not mask the real update validation result.
        }
    }
}

internal sealed record UpdatePackageSignatureVerificationResult(IReadOnlyList<string> VerifiedFiles);

internal sealed record UpdatePackageArchiveEntryMetadata(
    string FullName,
    long UncompressedLength,
    long CompressedLength);

internal sealed record ValidatedPackageArchiveEntry(int Index, string NormalizedPath, bool IsPeFile);

internal sealed record UpdatePackageArchiveValidationResult(
    IReadOnlyList<ValidatedPackageArchiveEntry> Entries,
    long TotalUncompressedBytes,
    int PeCount);

internal enum AuthorizedPackagePublisher
{
    None,
    Custodian,
    Microsoft,
    JetBrains
}

internal sealed record MicrosoftFileIdentityException(
    string? OriginalFileName,
    bool RequireMicrosoftCompanyName = true);

internal interface IAuthenticodeSignatureVerifier
{
    AuthenticodeSignatureResult Verify(string filePath);
}

internal sealed record AuthenticodeSignatureResult(
    bool IsTrusted,
    string? SignerSubject = null,
    string? SignerSimpleName = null,
    string? SignerOrganization = null,
    string? CompanyName = null,
    string? OriginalFileName = null,
    string? FailureReason = null);

internal sealed record WintrustVerificationPolicy(uint RevocationChecks, uint ProviderFlags);

internal sealed class WindowsAuthenticodeSignatureVerifier : IAuthenticodeSignatureVerifier
{
    internal static WintrustVerificationPolicy VerificationPolicy { get; } = new(
        (uint)WintrustRevocationChecks.WholeChain,
        (uint)WintrustProvFlags.RevocationCheckChainExcludeRoot);

    public AuthenticodeSignatureResult Verify(string filePath)
    {
        var trustResult = WinVerifyTrust(filePath);
        if (trustResult != 0)
        {
            return new AuthenticodeSignatureResult(false, FailureReason: $"WinVerifyTrust returned 0x{trustResult:X8}");
        }

        try
        {
#pragma warning disable SYSLIB0057
            using var signedCertificate = X509Certificate.CreateFromSignedFile(filePath);
            using var certificate = new X509Certificate2(signedCertificate);
#pragma warning restore SYSLIB0057
            var versionInfo = FileVersionInfo.GetVersionInfo(filePath);
            return new AuthenticodeSignatureResult(
                true,
                certificate.Subject,
                certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                GetDistinguishedNameAttribute(certificate.SubjectName, "O"),
                versionInfo.CompanyName,
                versionInfo.OriginalFilename);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return new AuthenticodeSignatureResult(false, FailureReason: ex.Message);
        }
    }

    private static string? GetDistinguishedNameAttribute(X500DistinguishedName subject, string attributeName)
    {
        var prefix = attributeName + "=";
        return subject
            .Decode(X500DistinguishedNameFlags.UseNewLines)
            .Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..]
            .Trim();
    }

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "WINTRUST_DATA uses mutable class fields and is simpler with DllImport here.")]
    private static int WinVerifyTrust(string filePath)
    {
        var fileInfo = new WintrustFileInfo(filePath);
        var data = new WintrustData(fileInfo);
        var action = WintrustActionGenericVerifyV2;
        try
        {
            return WinVerifyTrust(IntPtr.Zero, action, data);
        }
        finally
        {
            data.StateAction = WintrustStateAction.Close;
            try
            {
                _ = WinVerifyTrust(IntPtr.Zero, action, data);
            }
            finally
            {
                fileInfo.Dispose();
                data.Dispose();
            }
        }
    }

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        [In, Out]
        WintrustData data);

    private enum WintrustUnionChoice : uint
    {
        File = 1
    }

    private enum WintrustDataChoice : uint
    {
        None = 2
    }

    private enum WintrustRevocationChecks : uint
    {
        WholeChain = 1
    }

    private enum WintrustStateAction : uint
    {
        Ignore = 0,
        Verify = 1,
        Close = 2
    }

    [Flags]
    private enum WintrustProvFlags : uint
    {
        RevocationCheckChainExcludeRoot = 0x00000080
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WintrustFileInfo : IDisposable
    {
        public WintrustFileInfo(string filePath)
        {
            StructSize = (uint)Marshal.SizeOf<WintrustFileInfo>();
            FilePath = Marshal.StringToCoTaskMemUni(filePath);
        }

        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle = IntPtr.Zero;
        public IntPtr KnownSubject = IntPtr.Zero;

        public void Dispose()
        {
            if (FilePath != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(FilePath);
                FilePath = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class WintrustData : IDisposable
    {
        public WintrustData(WintrustFileInfo fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WintrustData>();
            FileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, FileInfo, fDeleteOld: false);
            UnionChoice = WintrustUnionChoice.File;
            StateAction = WintrustStateAction.Verify;
        }

        public uint StructSize;
        public IntPtr PolicyCallbackData = IntPtr.Zero;
        public IntPtr SipClientData = IntPtr.Zero;
        public WintrustDataChoice UiChoice = WintrustDataChoice.None;
        public WintrustRevocationChecks RevocationChecks = WintrustRevocationChecks.WholeChain;
        public WintrustUnionChoice UnionChoice;
        public IntPtr FileInfo;
        public WintrustStateAction StateAction;
        public IntPtr StateData = IntPtr.Zero;
        public IntPtr UrlReference = IntPtr.Zero;
        public WintrustProvFlags ProvFlags = WintrustProvFlags.RevocationCheckChainExcludeRoot;
        public uint UiContext = 0;

        public void Dispose()
        {
            if (FileInfo != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(FileInfo);
                FileInfo = IntPtr.Zero;
            }
        }
    }
}
