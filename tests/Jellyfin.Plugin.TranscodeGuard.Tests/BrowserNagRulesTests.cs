using Jellyfin.Plugin.TranscodeGuard.Browser;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

public class BrowserNagRulesTests
{
    private static readonly Guid BobId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Theory]
    [InlineData("Jellyfin Web", true)]
    [InlineData("jellyfin web", true)]
    [InlineData("JELLYFIN WEB", true)]
    [InlineData("Jellyfin Media Player", false)]
    [InlineData("Jellyfin MPV Shim", false)]
    [InlineData("Jellyfin Android TV", false)]
    [InlineData("Jellyfin Android", false)]
    [InlineData("Swiftfin", false)]
    [InlineData("Jellyfin Roku", false)]
    [InlineData("Jellyfin Web Wrapper", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsBrowserSession_IsAnAllowlistOnTheSessionClient(string? client, bool expected)
    {
        var session = Session("session-1", "device-1", client, "Firefox");

        Assert.Equal(expected, BrowserNagRules.IsBrowserSession(session));
    }

    [Theory]
    [InlineData("Firefox")]
    [InlineData("Chrome")]
    [InlineData("LibreWolf")]
    [InlineData("Vivaldi")]
    [InlineData("Brave")]
    [InlineData("Safari")]
    [InlineData("Floorp")]
    [InlineData("Some Future Browser")]
    public void IsBrowserSession_AcceptsAnyBrowserNameUnderJellyfinWeb(string deviceName)
    {
        Assert.True(BrowserNagRules.IsBrowserSession(Session("session-1", "device-1", "Jellyfin Web", deviceName)));
    }

    [Theory]
    [InlineData("Jellyfin Media Player")]
    [InlineData("Jellyfin MPV Shim")]
    public void IsBrowserSession_IgnoresABrowserLikeDeviceNameOnANativeClient(string client)
    {
        Assert.False(BrowserNagRules.IsBrowserSession(Session("session-1", "device-1", client, "Firefox")));
    }

    [Fact]
    public void IsBrowserSession_HandlesNoSession()
    {
        Assert.False(BrowserNagRules.IsBrowserSession(null));
    }

    [Fact]
    public void ShouldUseBrowserNag_RequiresTheFeatureAndJellyfinWeb()
    {
        var browser = Session("session-a", "device-a", "Jellyfin Web", "Firefox");
        var shim = Session("session-b", "device-b", "Jellyfin MPV Shim", "desktop");

        Assert.False(BrowserNagRules.ShouldUseBrowserNag(browser, new PluginConfiguration()));
        Assert.True(BrowserNagRules.ShouldUseBrowserNag(browser, Enabled()));
        Assert.False(BrowserNagRules.ShouldUseBrowserNag(shim, Enabled()));
        Assert.False(BrowserNagRules.ShouldUseBrowserNag(null, Enabled()));
    }

    [Fact]
    public void ShouldUseBrowserNag_DecidesPerSessionForOneUsersMixedClients()
    {
        // Bob: a browser, Jellyfin Media Player, and a second browser on another PC. The warning
        // is decided for the one session being nagged, never for the user as a whole.
        var browserA = Session("session-a", "device-a", "Jellyfin Web", "Firefox");
        var mediaPlayer = Session("session-b", "device-b", "Jellyfin Media Player", "desktop");
        var browserC = Session("session-c", "device-c", "Jellyfin Web", "Firefox");
        var config = Enabled();

        Assert.True(BrowserNagRules.ShouldUseBrowserNag(browserA, config));
        Assert.False(BrowserNagRules.ShouldUseBrowserNag(mediaPlayer, config));
        Assert.True(BrowserNagRules.ShouldUseBrowserNag(browserC, config));
    }

    [Theory]
    [InlineData("https://example.com/jellyfin-client", "https://example.com/jellyfin-client")]
    [InlineData("  https://example.com/get  ", "https://example.com/get")]
    [InlineData("http://clients.local:8080/", "http://clients.local:8080/")]
    [InlineData("HTTPS://Example.com", "https://example.com/")]
    public void NormalizeInstallUrl_AcceptsAbsoluteHttpAndHttps(string configured, string expected)
    {
        Assert.Equal(expected, BrowserNagRules.NormalizeInstallUrl(configured));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(document.cookie)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/client")]
    [InlineData("/web/index.html")]
    [InlineData("example.com/client")]
    [InlineData("//example.com/client")]
    [InlineData("https://")]
    public void NormalizeInstallUrl_RejectsEverythingElse(string? configured)
    {
        Assert.Null(BrowserNagRules.NormalizeInstallUrl(configured));
    }

    [Theory]
    [InlineData(int.MinValue, 5)]
    [InlineData(0, 5)]
    [InlineData(4, 5)]
    [InlineData(5, 5)]
    [InlineData(60, 60)]
    [InlineData(180, 180)]
    [InlineData(181, 180)]
    [InlineData(int.MaxValue, 180)]
    public void ClampAutoCloseSeconds_KeepsTheModalBetweenFiveSecondsAndThreeMinutes(int configured, int expected)
    {
        Assert.Equal(expected, BrowserNagRules.ClampAutoCloseSeconds(configured));
    }

    [Fact]
    public void DescribeReasons_ListsOnlyTheConfiguredReasonsInPlainWords()
    {
        var config = new PluginConfiguration();
        var reasons = TranscodeReason.VideoCodecNotSupported
            | TranscodeReason.AudioCodecNotSupported
            | TranscodeReason.ContainerBitrateExceedsLimit;

        Assert.Equal(
            "Video codec not supported, audio codec not supported",
            BrowserNagRules.DescribeReasons(reasons, config));
    }

    [Fact]
    public void DescribeReasons_IsEmptyWhenNoConfiguredReasonIsActive()
    {
        Assert.Equal(
            string.Empty,
            BrowserNagRules.DescribeReasons(TranscodeReason.ContainerBitrateExceedsLimit, new PluginConfiguration()));
    }

    [Fact]
    public void BuildArguments_CarriesEverythingTheScriptRenders()
    {
        var nagId = Guid.Parse("0123456789abcdef0123456789abcdef");
        var config = Enabled();
        config.BrowserInstallUrl = "https://example.com/client";
        config.BrowserNagTitle = "Use the app";
        config.BrowserNagMessage = "Your browser can't play this.";
        config.BrowserNagAutoCloseSeconds = 90;

        var arguments = BrowserNagRules.BuildArguments(config, TranscodeReason.VideoCodecNotSupported, nagId);

        Assert.Equal(BrowserNagRules.MarkerValue, arguments[BrowserNagRules.MarkerArgument]);
        Assert.Equal("0123456789abcdef0123456789abcdef", arguments[BrowserNagRules.NagIdArgument]);
        Assert.Equal("Use the app", arguments[BrowserNagRules.TitleArgument]);
        Assert.Equal("Your browser can't play this.", arguments[BrowserNagRules.MessageArgument]);
        Assert.Equal("Video codec not supported", arguments[BrowserNagRules.ReasonArgument]);
        Assert.Equal("https://example.com/client", arguments[BrowserNagRules.InstallUrlArgument]);
        Assert.Equal("90", arguments[BrowserNagRules.AutoCloseSecondsArgument]);
    }

    [Fact]
    public void BuildArguments_LeavesTheInstallButtonOutForAnUnsafeUrl()
    {
        var config = Enabled();
        config.BrowserInstallUrl = "javascript:alert(1)";

        var arguments = BrowserNagRules.BuildArguments(config, TranscodeReason.VideoCodecNotSupported, Guid.NewGuid());

        Assert.False(arguments.ContainsKey(BrowserNagRules.InstallUrlArgument));
    }

    [Fact]
    public void BuildArguments_FallsBackWhenTextIsBlankAndClampsTheTimeout()
    {
        var config = Enabled();
        config.BrowserNagTitle = "  ";
        config.BrowserNagMessage = string.Empty;
        config.BrowserNagAutoCloseSeconds = 3600;

        var arguments = BrowserNagRules.BuildArguments(config, 0, Guid.NewGuid());

        Assert.Equal("Transcoding detected", arguments[BrowserNagRules.TitleArgument]);
        Assert.Contains("install the recommended client", arguments[BrowserNagRules.MessageArgument], StringComparison.Ordinal);
        Assert.Equal("180", arguments[BrowserNagRules.AutoCloseSecondsArgument]);
        Assert.False(arguments.ContainsKey(BrowserNagRules.ReasonArgument));
    }

    [Fact]
    public void BuildArguments_NeverUsesTheStandardDisplayMessageArgumentNames()
    {
        var config = Enabled();
        config.BrowserInstallUrl = "https://example.com";

        var arguments = BrowserNagRules.BuildArguments(config, TranscodeReason.VideoCodecNotSupported, Guid.NewGuid());

        Assert.All(arguments.Keys, key => Assert.StartsWith("TranscodeGuard", key, StringComparison.Ordinal));
    }

    private static PluginConfiguration Enabled() => new() { EnableBrowserInstallNag = true };

    private static SessionInfo Session(string id, string deviceId, string? client, string deviceName)
    {
        var session = TestSessions.Create(id, deviceId, BobId, "bob");
        session.Client = client!;
        session.DeviceName = deviceName;
        return session;
    }
}
