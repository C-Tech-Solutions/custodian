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
    private static readonly string[] PackagePeExtensions = [".dll", ".exe"];

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
        var verifiedFiles = new List<string>();
        foreach (var entry in archive.Entries.Where(IsCustodianOwnedPeFile))
        {
            var tempPath = ExtractToTemporaryFile(entry);
            try
            {
                var result = signatureVerifier.Verify(tempPath);
                if (!result.IsTrusted)
                {
                    throw new InvalidOperationException(
                        $"Update package file '{entry.FullName}' is not trusted: {result.FailureReason ?? "signature verification failed"}.");
                }

                if (!IsTrustedSigner(result.SignerSubject, result.SignerSimpleName))
                {
                    throw new InvalidOperationException(
                        $"Update package file '{entry.FullName}' was signed by '{result.SignerSubject ?? result.SignerSimpleName ?? "unknown"}', not '{TrustedSignerName}'.");
                }

                verifiedFiles.Add(entry.FullName);
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        if (verifiedFiles.Count == 0)
        {
            throw new InvalidOperationException("The update package did not contain any Custodian-owned executable files to verify.");
        }

        return new UpdatePackageSignatureVerificationResult(verifiedFiles);
    }

    internal static bool IsTrustedSigner(string? subject, string? simpleName)
    {
        if (string.Equals(simpleName, TrustedSignerName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return DistinguishedNameContains(subject, "CN", TrustedSignerName) ||
            DistinguishedNameContains(subject, "O", TrustedSignerName);
    }

    private static bool IsCustodianOwnedPeFile(ZipArchiveEntry entry)
    {
        var fileName = Path.GetFileName(entry.FullName);
        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.StartsWith("Custodian.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);
        return PackagePeExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static string ExtractToTemporaryFile(ZipArchiveEntry entry)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "custodian-update-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var tempPath = Path.Combine(tempDirectory, Path.GetFileName(entry.FullName));
        entry.ExtractToFile(tempPath);
        return tempPath;
    }

    private static bool DistinguishedNameContains(string? subject, string attributeName, string expectedValue)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        var prefix = attributeName + "=";
        foreach (var part in subject.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(part[prefix.Length..].Trim(), expectedValue, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

internal interface IAuthenticodeSignatureVerifier
{
    AuthenticodeSignatureResult Verify(string filePath);
}

internal sealed record AuthenticodeSignatureResult(
    bool IsTrusted,
    string? SignerSubject = null,
    string? SignerSimpleName = null,
    string? FailureReason = null);

internal sealed class WindowsAuthenticodeSignatureVerifier : IAuthenticodeSignatureVerifier
{
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
            return new AuthenticodeSignatureResult(
                true,
                certificate.Subject,
                certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return new AuthenticodeSignatureResult(false, FailureReason: ex.Message);
        }
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
            _ = WinVerifyTrust(IntPtr.Zero, action, data);
            fileInfo.Dispose();
            data.Dispose();
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
        None = 0
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
        Safer = 0x00000100
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
        public WintrustRevocationChecks RevocationChecks = WintrustRevocationChecks.None;
        public WintrustUnionChoice UnionChoice;
        public IntPtr FileInfo;
        public WintrustStateAction StateAction;
        public IntPtr StateData = IntPtr.Zero;
        public IntPtr UrlReference = IntPtr.Zero;
        public WintrustProvFlags ProvFlags = WintrustProvFlags.Safer;
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
