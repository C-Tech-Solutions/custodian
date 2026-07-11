namespace Custodian.App.Services;

internal sealed record AboutInfo(string Version, string RepositoryUrl);

internal static class AboutInfoProvider
{
    internal const string RepositoryUrl = "https://github.com/ctech1313/custodian";
    internal const string UnknownVersion = "Unknown";

    public static AboutInfo GetCurrent()
        => Create(WhatsNewLinkBuilder.BuildCurrentVersionTag());

    internal static AboutInfo Create(string? informationalVersion)
        => new(
            WhatsNewLinkBuilder.NormalizeVersionTag(informationalVersion) ?? UnknownVersion,
            RepositoryUrl);
}
