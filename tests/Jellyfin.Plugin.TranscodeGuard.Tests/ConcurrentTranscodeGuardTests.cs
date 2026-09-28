using Jellyfin.Plugin.TranscodeGuard.Browser;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using Jellyfin.Plugin.TranscodeGuard.Limits;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

public class ConcurrentTranscodeGuardTests
{
    private static readonly Guid AliceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BobId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid MovieId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task AllowsEverythingWhileTheLimitIsSwitchedOff()
    {
        var harness = new ConcurrencyHarness(new PluginConfiguration { EnableConcurrentTranscodeLimit = false, MaxConcurrentTranscodes = 1 });
        await harness.LaunchAsync(Request("tv"));

        var decision = await harness.AssessAsync(Request("phone"));

        Assert.True(decision.IsAdmitted);
        Assert.Empty(harness.Messages.SentMessages);
    }

    [Fact]
    public async Task SwitchedOnWithoutAnyMaximumLimitsNothing()
    {
        var harness = new ConcurrencyHarness(new PluginConfiguration { EnableConcurrentTranscodeLimit = true });
        await harness.LaunchAsync(Request("tv"));
        await harness.LaunchAsync(Request("phone"));
        await harness.LaunchAsync(Request("laptop"));

        Assert.True((await harness.AssessAsync(Request("tablet"))).IsAdmitted);
    }

    [Fact]
    public async Task RefusesTheDeviceThatWouldGoOverTheGlobalMaximumAndSaysWhy()
    {
        var config = Enabled(globalMaximum: 2);
        config.ConcurrentTranscodeLimitHeader = "Busy";
        config.ConcurrentTranscodeLimitMessage = "{{active}} running, {{limit}} allowed.";
        var harness = new ConcurrencyHarness(config);
        harness.Messages.AddSession(TestSessions.Create("session-laptop", "laptop", AliceId));
        await harness.LaunchAsync(Request("tv"));
        await harness.LaunchAsync(Request("phone"));

        var decision = await harness.AssessAsync(Request("laptop"));

        Assert.False(decision.IsAdmitted);
        Assert.Equal(2, decision.ActiveTranscodes);
        Assert.Equal(2, decision.Limit);
        Assert.Contains("2 video transcode(s) running on other devices", decision.BuildRefusalReason(), StringComparison.Ordinal);
        Assert.Contains("limit of 2", decision.BuildRefusalReason(), StringComparison.Ordinal);

        var sent = Assert.Single(harness.Messages.SentMessages);
        Assert.Equal("session-laptop", sent.Session.Id);
        Assert.Equal("Busy", sent.Command.Header);
        Assert.Equal("2 running, 2 allowed.", sent.Command.Text);
        Assert.False(sent.UseStickyMessages);
    }

    [Fact]
    public async Task TheRequestingDevicesOwnTranscodeNeverCountsAgainstIt()
    {
        // A seek, a track change, or the next episode starts a fresh FFmpeg job on a device that is
        // already transcoding. It replaces that device's transcode rather than adding one.
        var harness = new ConcurrencyHarness(Enabled(globalMaximum: 1));
        await harness.LaunchAsync(Request("tv", playSessionId: "episode-1"));

        Assert.True((await harness.AssessAsync(Request("tv", playSessionId: "episode-1"))).IsAdmitted);
        Assert.True((await harness.AssessAsync(Request("tv", playSessionId: "episode-2"))).IsAdmitted);
    }

    [Fact]
    public async Task ADeviceWithSeveralJobsStillCountsOnce()
    {
        var harness = new ConcurrencyHarness(Enabled(globalMaximum: 2));

        // A job from the previous episode that has not been reaped yet sits next to the new one.
        await harness.LaunchAsync(Request("tv", playSessionId: "episode-1"));
        await harness.LaunchAsync(Request("tv", playSessionId: "episode-2"));

        Assert.True((await harness.AssessAsync(Request("phone"))).IsAdmitted);
    }

    [Fact]
    public async Task DeviceIdsAreComparedTheWayJellyfinComparesThem()
    {
        var harness = new ConcurrencyHarness(Enabled(globalMaximum: 1));
        await harness.LaunchAsync(Request("TV-Living-Room"));

        Assert.True((await harness.AssessAsync(Request("tv-living-room"))).IsAdmitted);
    }

    [Fact]
    public async Task APerUserMaximumAboveTheGlobalOneWins()
    {
        var config = Enabled(globalMaximum: 1);
        config.UserConcurrentTranscodeLimits = new[] { UserMaximum(AliceId, 3) };
        var harness = new ConcurrencyHarness(config);
        await harness.LaunchAsync(Request("tv"));
        await harness.LaunchAsync(Request("phone"));

        Assert.True((await harness.AssessAsync(Request("laptop"))).IsAdmitted);

        await harness.LaunchAsync(Request("laptop"));
        var refused = await harness.AssessAsync(Request("tablet"));
        Assert.False(refused.IsAdmitted);
        Assert.Equal(3, refused.Limit);
    }

    [Fact]
    public async Task APerUserMaximumBelowTheGlobalOneWins()
    {
        var config = Enabled(globalMaximum: 3);
        config.UserConcurrentTranscodeLimits = new[] { UserMaximum(BobId, 1) };
        var harness = new ConcurrencyHarness(config);
        await harness.LaunchAsync(Request("bob-tv", BobId));
        await harness.LaunchAsync(Request("alice-tv", AliceId));

        var bob = await harness.AssessAsync(Request("bob-phone", BobId));
        Assert.False(bob.IsAdmitted);
        Assert.Equal(1, bob.Limit);

        // Everyone else is still on the global maximum.
        Assert.True((await harness.AssessAsync(Request("alice-phone", AliceId))).IsAdmitted);
    }

    [Fact]
    public async Task APerUserMaximumWorksWithoutAnyGlobalMaximum()
    {
        var config = Enabled(globalMaximum: 0);
        config.UserConcurrentTranscodeLimits = new[] { UserMaximum(BobId, 1) };
        var harness = new ConcurrencyHarness(config);
        await harness.LaunchAsync(Request("bob-tv", BobId));
        await harness.LaunchAsync(Request("alice-tv", AliceId));
        await harness.LaunchAsync(Request("alice-phone", AliceId));

        Assert.False((await harness.AssessAsync(Request("bob-phone", BobId))).IsAdmitted);
        Assert.True((await harness.AssessAsync(Request("alice-laptop", AliceId))).IsAdmitted);
    }

    [Fact]
    public async Task UsersAreCountedSeparately()
    {
        var harness = new ConcurrencyHarness(Enabled(globalMaximum: 1));
        await harness.LaunchAsync(Request("alice-tv", AliceId));

        Assert.True((await harness.AssessAsync(Request("bob-tv", BobId))).IsAdmitted);
    }

    [Fact]
    public void ResolveLimit_PerUserEntryWinsAndAcceptsBothGuidFormats()
    {
        var config = Enabled(globalMaximum: 2);
        config.UserConcurrentTranscodeLimits = new[]
        {
            new UserConcurrentTranscodeLimit { UserId = AliceId.ToString("N"), MaxTranscodes = 5 },
            new UserConcurrentTranscodeLimit { UserId = " " + BobId.ToString("D").ToUpperInvariant() + " ", MaxTranscodes = 1 }
        };

        Assert.Equal(5, ConcurrentTranscodeGuard.ResolveLimit(config, AliceId));
        Assert.Equal(1, ConcurrentTranscodeGuard.ResolveLimit(config, BobId));
        Assert.Equal(2, ConcurrentTranscodeGuard.ResolveLimit(config, Guid.NewGuid()));
    }

    [Fact]
    public void ResolveLimit_UnusableEntriesFallBackToTheGlobalMaximum()
    {
        // Below 1 would refuse every transcode, which is never what an admin means by a maximum.
        var config = Enabled(globalMaximum: 2);
        config.UserConcurrentTranscodeLimits = new UserConcurrentTranscodeLimit[]
        {
            new() { UserId = AliceId.ToString("N"), MaxTranscodes = 0 },
            new() { UserId = "not-a-guid", MaxTranscodes = 4 },
            null!
        };

        Assert.Equal(2, ConcurrentTranscodeGuard.ResolveLimit(config, AliceId));

        config.MaxConcurrentTranscodes = -3;
        Assert.Equal(0, ConcurrentTranscodeGuard.ResolveLimit(config, AliceId));

        config.UserConcurrentTranscodeLimits = null!;
        Assert.Equal(0, ConcurrentTranscodeGuard.ResolveLimit(config, AliceId));
    }

    [Fact]
    public async Task AFinishedTranscodeStopsCounting()
    {
        var harness = new ConcurrencyHarness(Enabled(globalMaximum: 1));
        var job = await harness.LaunchAsync(Request("tv"));

        Assert.False((await harness.AssessAsync(Request("phone"))).IsAdmitted);

        job.HasExited = true;

        Assert.True((await harness.AssessAsync(Request("phone"))).IsAdmitted);
    }

    [Fact]
    public async Task AnAdmittedTranscodeHoldsItsPlaceBeforeFfmpegLaunches()
    {
        // Two devices pressing play together must not both be admitted into the last place.
        var harness = new ConcurrencyHarness(Enabled(globalMaximum: 1));
        var first = await harness.AssessAsync(Request("tv"));
        Assert.True(first.IsAdmitted);

        Assert.False((await harness.AssessAsync(Request("phone"))).IsAdmitted);
    }

    [Fact]
    public async Task AnAdmittedTranscodeThatNeverLaunchedGivesItsPlaceBack()
    {
        var harness = new ConcurrencyHarness(Enabled(globalMaximum: 1));
        var first = await harness.AssessAsync(Request("tv"));

        first.Slot!.Dispose();

        Assert.True((await harness.AssessAsync(Request("phone"))).IsAdmitted);
    }

    [Fact]
    public async Task ReleasingALaunchedSlotDoesNotUncountItsRunningJob()
    {
        // The decorator disposes the slot on its way out whatever happened; after a launch that
        // must be a no-op, or every transcode would stop counting the moment it started.
        var harness = new ConcurrencyHarness(Enabled(globalMaximum: 1));
        var decision = await harness.AssessAsync(Request("tv"));
        decision.Slot!.MarkLaunched(new TranscodingJob(NullLogger<TranscodingJob>.Instance));

        decision.Slot.Dispose();

        Assert.False((await harness.AssessAsync(Request("phone"))).IsAdmitted);
    }

    [Theory]
    [InlineData(true, "copy")]
    [InlineData(true, "COPY")]
    [InlineData(false, "h264")]
    public async Task RemuxAndAudioOnlyAreNeverCountedOrRefused(bool isVideoRequest, string outputVideoCodec)
    {
        var harness = new ConcurrencyHarness(Enabled(globalMaximum: 1));

        var notATranscode = Request("tv");
        notATranscode.IsVideoRequest = isVideoRequest;
        notATranscode.OutputVideoCodec = outputVideoCodec;

        var decision = await harness.AssessAsync(notATranscode);
        Assert.True(decision.IsAdmitted);
        Assert.Null(decision.Slot);

        await harness.LaunchAsync(Request("phone"));
        Assert.True((await harness.AssessAsync(notATranscode)).IsAdmitted);
    }

    [Fact]
    public async Task BitrateOnlyAndLiveTvTranscodesCount()
    {
        // A resource cap, not a nag: the nag's trigger reasons and Live TV exclusion do not apply.
        var config = Enabled(globalMaximum: 1);
        config.ExcludeLiveTv = true;
        var harness = new ConcurrencyHarness(config);

        var liveTv = Request("tv");
        liveTv.IsLiveStream = true;
        liveTv.TranscodeReasons = 0;
        await harness.LaunchAsync(liveTv);

        var bitrateOnly = Request("phone");
        bitrateOnly.TranscodeReasons = 0;
        Assert.False((await harness.AssessAsync(bitrateOnly)).IsAdmitted);
    }

    [Fact]
    public async Task NagExclusionsDoNotExemptAUser()
    {
        var config = Enabled(globalMaximum: 1);
        config.ExcludedUserIds = new[] { AliceId.ToString("N") };
        config.ExcludedClientPatterns = new[] { "Jellyfin Web" };
        var harness = new ConcurrencyHarness(config);
        harness.Messages.AddSession(TestSessions.Create("session-phone", "phone", AliceId));
        await harness.LaunchAsync(Request("tv"));

        Assert.False((await harness.AssessAsync(Request("phone"))).IsAdmitted);
    }

    [Fact]
    public async Task TranscodesStartedWhileTheLimitWasOffCountOnceItIsSwitchedOn()
    {
        var config = new PluginConfiguration { EnableConcurrentTranscodeLimit = false, MaxConcurrentTranscodes = 1 };
        var harness = new ConcurrencyHarness(config);
        await harness.LaunchAsync(Request("tv"));

        config.EnableConcurrentTranscodeLimit = true;

        Assert.False((await harness.AssessAsync(Request("phone"))).IsAdmitted);
    }

    [Fact]
    public async Task AllowsAnUnauthenticatedRequest()
    {
        var harness = new ConcurrencyHarness(Enabled(globalMaximum: 1));
        await harness.LaunchAsync(Request("tv"));

        var decision = await harness.AssessAsync(Request("phone", Guid.Empty));

        Assert.True(decision.IsAdmitted);
        Assert.Null(decision.Slot);
    }

    [Fact]
    public async Task AllowsEverythingBeforeThePluginConfigurationIsLoaded()
    {
        var harness = new ConcurrencyHarness(null);
        await harness.LaunchAsync(Request("tv"));

        Assert.True((await harness.AssessAsync(Request("phone"))).IsAdmitted);
    }

    [Fact]
    public async Task ATranscodeWithNoDeviceIdIsItsOwnDevice()
    {
        var harness = new ConcurrencyHarness(Enabled(globalMaximum: 1));
        await harness.LaunchAsync(Request(null, playSessionId: null));

        // It counts against everything else, and without a device it excuses nothing else.
        Assert.False((await harness.AssessAsync(Request("tv"))).IsAdmitted);
        Assert.False((await harness.AssessAsync(Request(null, playSessionId: null))).IsAdmitted);
    }

    [Fact]
    public async Task APlaySessionStandsInForAMissingDeviceId()
    {
        var harness = new ConcurrencyHarness(Enabled(globalMaximum: 1));
        await harness.LaunchAsync(Request(null, playSessionId: "play-1"));

        Assert.True((await harness.AssessAsync(Request(null, playSessionId: "play-1"))).IsAdmitted);
        Assert.False((await harness.AssessAsync(Request(null, playSessionId: "play-2"))).IsAdmitted);
    }

    [Fact]
    public async Task ARetryBurstProducesOnePopupButStaysRefused()
    {
        var now = DateTimeOffset.Parse("2026-09-28T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var harness = new ConcurrencyHarness(Enabled(globalMaximum: 1), () => now);
        harness.Messages.AddSession(TestSessions.Create("session-phone", "phone", AliceId));
        await harness.LaunchAsync(Request("tv"));

        Assert.False((await harness.AssessAsync(Request("phone"))).IsAdmitted);
        now = now.AddMilliseconds(400);
        Assert.False((await harness.AssessAsync(Request("phone"))).IsAdmitted);
        now = now.AddMilliseconds(400);
        Assert.False((await harness.AssessAsync(Request("phone"))).IsAdmitted);

        Assert.Single(harness.Messages.SentMessages);

        // A deliberate press of play after a lull is announced again.
        now = now.AddSeconds(10);
        Assert.False((await harness.AssessAsync(Request("phone"))).IsAdmitted);
        Assert.Equal(2, harness.Messages.SentMessages.Count);
    }

    [Fact]
    public async Task BlankTitleAndMessageFallBackToUsableText()
    {
        var config = Enabled(globalMaximum: 1);
        config.ConcurrentTranscodeLimitHeader = " ";
        config.ConcurrentTranscodeLimitMessage = string.Empty;
        config.UseStickyConcurrentTranscodeLimitMessages = true;
        var harness = new ConcurrencyHarness(config);
        harness.Messages.AddSession(TestSessions.Create("session-phone", "phone", AliceId));
        await harness.LaunchAsync(Request("tv"));

        await harness.AssessAsync(Request("phone"));

        var sent = Assert.Single(harness.Messages.SentMessages);
        Assert.Equal("Too many transcodes at once", sent.Command.Header);
        Assert.False(string.IsNullOrWhiteSpace(sent.Command.Text));
        Assert.True(sent.UseStickyMessages);
    }

    [Fact]
    public void DefaultMessageFillsInBothPlaceholders()
    {
        var text = TranscodeGuardRules.FormatConcurrentTranscodeLimitMessage(
            new PluginConfiguration().ConcurrentTranscodeLimitMessage,
            activeTranscodes: 2,
            limit: 2);

        Assert.Contains("already transcoding 2 video(s)", text, StringComparison.Ordinal);
        Assert.Contains("allows 2 at a time", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusalToJellyfinWebCarriesTheBrowserWarningWithTheRefusalText()
    {
        var config = Enabled(globalMaximum: 1);
        config.ConcurrentTranscodeLimitHeader = "Busy";
        config.ConcurrentTranscodeLimitMessage = "{{active}} of {{limit}}.";
        config.EnableBrowserInstallNag = true;
        config.BrowserInstallUrl = "https://example.com/client";
        var harness = new ConcurrencyHarness(config);
        harness.Messages.AddSession(TestSessions.Create("session-phone", "phone", AliceId));
        await harness.LaunchAsync(Request("tv"));

        var request = Request("phone");
        request.TranscodeReasons = TranscodeReason.VideoCodecNotSupported;
        Assert.False((await harness.AssessAsync(request)).IsAdmitted);

        var arguments = Assert.Single(harness.Messages.SentExtraArguments);
        Assert.NotNull(arguments);
        Assert.Equal("Busy", arguments[BrowserNagRules.TitleArgument]);
        Assert.Equal("1 of 1.", arguments[BrowserNagRules.MessageArgument]);
        Assert.Equal(BrowserNagRules.RefusalDismissLabel, arguments[BrowserNagRules.DismissLabelArgument]);
        Assert.Equal("Video codec not supported", arguments[BrowserNagRules.ReasonArgument]);
        Assert.Equal("https://example.com/client", arguments[BrowserNagRules.InstallUrlArgument]);
    }

    [Fact]
    public async Task AFailingPopupDoesNotReverseTheRefusal()
    {
        var config = Enabled(globalMaximum: 1);
        var guard = new ConcurrentTranscodeGuard(
            new ThrowingClientMessageService(TestSessions.Create("session-phone", "phone", AliceId)),
            new ThrowingLogger<ConcurrentTranscodeGuard>(),
            () => config);
        using var cts = new CancellationTokenSource();

        var first = await guard.AssessAsync(Request("tv"), cts.Token);
        first.Slot!.MarkLaunched(new TranscodingJob(NullLogger<TranscodingJob>.Instance));

        var decision = await guard.AssessAsync(Request("phone"), cts.Token);

        Assert.False(decision.IsAdmitted);
    }

    [Fact]
    public async Task AnUncorrelatedRequestIsStillRefused()
    {
        var harness = new ConcurrencyHarness(Enabled(globalMaximum: 1));
        await harness.LaunchAsync(Request("tv"));

        // No session for "phone": nobody to show a popup to, but the limit still holds.
        Assert.False((await harness.AssessAsync(Request("phone"))).IsAdmitted);
        Assert.Empty(harness.Messages.SentMessages);
    }

    private static PluginConfiguration Enabled(int globalMaximum)
    {
        return new PluginConfiguration
        {
            EnableConcurrentTranscodeLimit = true,
            MaxConcurrentTranscodes = globalMaximum
        };
    }

    private static UserConcurrentTranscodeLimit UserMaximum(Guid userId, int maximum)
        => new() { UserId = userId.ToString("N"), MaxTranscodes = maximum };

    private static TranscodeLimitRequest Request(string? deviceId, Guid? userId = null, string? playSessionId = null)
    {
        return new TranscodeLimitRequest
        {
            IsVideoRequest = true,
            OutputVideoCodec = "h264",
            TranscodeReasons = TranscodeReason.VideoCodecNotSupported,
            DeviceId = deviceId,
            PlaySessionId = playSessionId ?? (deviceId == null ? null : "play-" + deviceId),
            UserId = userId ?? AliceId,
            ItemId = MovieId,
            ItemName = "Movie"
        };
    }

    private sealed class ConcurrencyHarness
    {
        private readonly ConcurrentTranscodeGuard _guard;

        public ConcurrencyHarness(PluginConfiguration? config, Func<DateTimeOffset>? clock = null)
        {
            _guard = new ConcurrentTranscodeGuard(
                Messages,
                NullLogger<ConcurrentTranscodeGuard>.Instance,
                () => config,
                TimeSpan.FromSeconds(3),
                clock ?? (() => DateTimeOffset.UtcNow));
        }

        public RecordingClientMessageService Messages { get; } = new();

        public Task<ConcurrentTranscodeDecision> AssessAsync(TranscodeLimitRequest request)
            => _guard.AssessAsync(request, CancellationToken.None);

        /// <summary>
        /// Admits and launches a transcode the way the decorator does, returning its job so a test
        /// can end it.
        /// </summary>
        public async Task<TranscodingJob> LaunchAsync(TranscodeLimitRequest request)
        {
            var decision = await AssessAsync(request);
            Assert.True(decision.IsAdmitted);

            var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance);
            decision.Slot!.MarkLaunched(job);
            return job;
        }
    }
}
