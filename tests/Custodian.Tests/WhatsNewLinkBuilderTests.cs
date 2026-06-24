using Custodian.App.Services;

namespace Custodian.Tests;

public sealed class WhatsNewLinkBuilderTests
{
    [Fact]
    public void BuildReleaseNotesUrl_UsesVersionTag()
    {
        var url = WhatsNewLinkBuilder.BuildReleaseNotesUrl("1.3.0");

        Assert.Equal("https://github.com/ctech1313/custodian/releases/tag/1.3.0", url);
    }

    [Fact]
    public void BuildReleaseNotesUrl_StripsBuildMetadataAndPreservesPrerelease()
    {
        var url = WhatsNewLinkBuilder.BuildReleaseNotesUrl("1.4.0-preview.1+abc123");

        Assert.Equal("https://github.com/ctech1313/custodian/releases/tag/1.4.0-preview.1", url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+abc123")]
    public void BuildReleaseNotesUrl_FallsBackToChangelogWhenVersionUnavailable(string? informationalVersion)
    {
        var url = WhatsNewLinkBuilder.BuildReleaseNotesUrl(informationalVersion);

        Assert.Equal(WhatsNewLinkBuilder.ChangelogUrl, url);
    }

    [Theory]
    [InlineData("1.4.0", null, true)]
    [InlineData("1.4.0", "", true)]
    [InlineData("1.4.0", "1.3.0", true)]
    [InlineData("1.4.0", "1.4.0", false)]
    [InlineData("1.4.0", " 1.4.0 ", false)]
    [InlineData(null, "1.3.0", false)]
    [InlineData("", "1.3.0", false)]
    [InlineData("   ", "1.3.0", false)]
    public void ShouldShowForVersion_OnlyShowsOncePerAvailableVersion(
        string? currentVersionTag,
        string? lastSeenVersion,
        bool expected)
    {
        var shouldShow = WhatsNewPromptPolicy.ShouldShowForVersion(currentVersionTag, lastSeenVersion);

        Assert.Equal(expected, shouldShow);
    }
}
