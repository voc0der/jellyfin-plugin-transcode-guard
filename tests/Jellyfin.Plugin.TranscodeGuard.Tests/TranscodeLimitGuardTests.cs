using Jellyfin.Plugin.TranscodeGuard.Configuration;
using Jellyfin.Plugin.TranscodeGuard.Limits;
using Jellyfin.Plugin.TranscodeGuard.Messaging;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

public class TranscodeLimitGuardTests
{
    private static readonly Guid AliceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MovieId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string DeviceId = "device-1";

    [Fact]
    public async Task AllowsEverythingWhileTheLimitIsSwitchedOff()
    {
        using var harness = new LimitHarness(new PluginConfiguration { EnableTranscodeLimit = false, TranscodeLimitThreshold = 1 });
        await harness.RecordBadTranscodesAsync(AliceId, 9);

        var decision = await harness.AssessAsync(Request());

        Assert.True(decision.IsAdmitted);
        Assert.Empty(harness.Messages.SentMessages);
    }

    [Fact]
    public async Task AllowsAUserBelowTheLimit()
    {
        using var harness = new LimitHarness(EnabledConfig(threshold: 10));
        await harness.RecordBadTranscodesAsync(AliceId, 9);

        var decision = await harness.AssessAsync(Request());

        Assert.True(decision.IsAdmitted);
        Assert.Empty(harness.Messages.SentMessages);
    }

    [Fact]
    public async Task RefusesAtTheLimitAndTellsTheClientWithTheConfiguredNumbers()
    {
        var config = EnabledConfig(threshold: 10);
        config.TranscodeLimitHeader = "Stop";
        config.TranscodeLimitMessage = "{{transcodes}} of {{limit}} this {{timewindow}}.";
        using var harness = new LimitHarness(config);
        await harness.RecordBadTranscodesAsync(AliceId, 10);

        var decision = await harness.AssessAsync(Request());

        Assert.False(decision.IsAdmitted);
        Assert.Equal(10, decision.TranscodeCount);
        Assert.Equal(10, decision.Threshold);
        Assert.Equal("week", decision.TimeWindowLabel);
        Assert.Contains("10 counted transcodes", decision.BuildRefusalReason(), StringComparison.Ordinal);

        var sent = Assert.Single(harness.Messages.SentMessages);
        Assert.Equal("Stop", sent.Command.Header);
        Assert.Equal("10 of 10 this week.", sent.Command.Text);
    }

    [Fact]
    public async Task CountsTheSameMonthLongWindowTheLoginNagUses()
    {
        var config = EnabledConfig(threshold: 3);
        config.LoginNagTimeWindow = "Month";
        using var harness = new LimitHarness(config);

        // Older than a week, inside a month: only the month-long window sees these.
        await harness.RecordBadTranscodesAsync(AliceId, 3, DateTime.UtcNow.AddDays(-20));

        var decision = await harness.AssessAsync(Request());

        Assert.False(decision.IsAdmitted);
        Assert.Equal("month", decision.TimeWindowLabel);
    }

    [Fact]
    public async Task NeverRefusesABitrateOnlyTranscode()
    {
        using var harness = new LimitHarness(EnabledConfig(threshold: 1));
        await harness.RecordBadTranscodesAsync(AliceId, 5);

        var decision = await harness.AssessAsync(Request(reasons: 0));

        Assert.True(decision.IsAdmitted);
        Assert.Empty(harness.Messages.SentMessages);
    }

    [Fact]
    public async Task NeverRefusesATranscodeForAReasonTheAdminDidNotSelect()
    {
        var config = EnabledConfig(threshold: 1);
        config.AlertTranscodeReasons = new[] { nameof(TranscodeReason.VideoCodecNotSupported) };
        using var harness = new LimitHarness(config);
        await harness.RecordBadTranscodesAsync(AliceId, 5);

        var decision = await harness.AssessAsync(Request(reasons: TranscodeReason.ContainerBitrateExceedsLimit));

        Assert.True(decision.IsAdmitted);
    }

    [Fact]
    public async Task NeverRefusesAnAudioOnlyStream()
    {
        using var harness = new LimitHarness(EnabledConfig(threshold: 1));
        await harness.RecordBadTranscodesAsync(AliceId, 5);

        var decision = await harness.AssessAsync(Request(isVideoRequest: false));

        Assert.True(decision.IsAdmitted);
    }

    [Fact]
    public async Task NeverRefusesAnExcludedUser()
    {
        var config = EnabledConfig(threshold: 1);
        config.ExcludedUserIds = new[] { AliceId.ToString() };
        using var harness = new LimitHarness(config);
        await harness.RecordBadTranscodesAsync(AliceId, 5);

        var decision = await harness.AssessAsync(Request());

        Assert.True(decision.IsAdmitted);
    }

    [Fact]
    public async Task NeverRefusesAClientWhoseHistoryIsFilteredOut()
    {
        var config = EnabledConfig(threshold: 1);
        config.ExcludedClientPatterns = new[] { "Jellyfin Web" };
        using var harness = new LimitHarness(config);
        await harness.RecordBadTranscodesAsync(AliceId, 5, client: "Kodi");

        var decision = await harness.AssessAsync(Request());

        Assert.True(decision.IsAdmitted);
        Assert.Empty(harness.Messages.SentMessages);
    }

    [Fact]
    public async Task NeverRefusesLiveTvWhileLiveTvIsExcluded()
    {
        var config = EnabledConfig(threshold: 1);
        config.ExcludeLiveTv = true;
        using var harness = new LimitHarness(config);
        await harness.RecordBadTranscodesAsync(AliceId, 5);

        Assert.True((await harness.AssessAsync(Request(isLiveStream: true))).IsAdmitted);
        Assert.False((await harness.AssessAsync(Request())).IsAdmitted);
    }

    [Fact]
    public async Task RefusesNothingWhileTheThresholdIsBelowOne()
    {
        using var harness = new LimitHarness(EnabledConfig(threshold: 0));

        var decision = await harness.AssessAsync(Request());

        Assert.True(decision.IsAdmitted);
    }

    [Fact]
    public async Task RefusesAnUnknownUserNever()
    {
        using var harness = new LimitHarness(EnabledConfig(threshold: 1));
        await harness.RecordBadTranscodesAsync(AliceId, 5);

        var decision = await harness.AssessAsync(Request(userId: Guid.Empty));

        Assert.True(decision.IsAdmitted);
    }

    [Fact]
    public async Task LetsAnAlreadyPlayingStreamKeepGoingWhenItsOwnPlaybackCrossedTheLimit()
    {
        // The event recorded for the current movie is what puts its owner over. Seeking starts a
        // fresh FFmpeg job, and refusing that would cut off the very film that reached the limit.
        using var harness = new LimitHarness(EnabledConfig(threshold: 5));
        await harness.RecordBadTranscodesAsync(AliceId, 5);
        harness.SetNowPlaying(MovieId);

        Assert.True((await harness.AssessAsync(Request())).IsAdmitted);
        Assert.Empty(harness.Messages.SentMessages);

        // Anything else they start is still refused.
        Assert.False((await harness.AssessAsync(Request(itemId: Guid.NewGuid()))).IsAdmitted);
    }

    [Fact]
    public async Task RefusesAFirstLaunchThatHasNotReportedPlaybackYet()
    {
        using var harness = new LimitHarness(EnabledConfig(threshold: 5));
        await harness.RecordBadTranscodesAsync(AliceId, 5);

        // No now-playing item: the client has requested the stream but not started it.
        Assert.False((await harness.AssessAsync(Request())).IsAdmitted);
    }

    [Fact]
    public async Task KeepsTheRefusalWhenTheClientPopupCannotBeDelivered()
    {
        // ClientMessageService does not catch every failure Jellyfin's WebSocket send can raise.
        // One escaping here would reach the decorator's fail-open catch and admit the transcode.
        using var harness = new LimitHarness(
            EnabledConfig(threshold: 1),
            messages: new ThrowingClientMessageService(TestSessions.Create("session-1", DeviceId, AliceId)));
        await harness.RecordBadTranscodesAsync(AliceId, 5);

        var decision = await harness.AssessAsync(Request());

        Assert.False(decision.IsAdmitted);
    }

    [Fact]
    public async Task CollapsesARetryBurstToOnePopupWithoutSofteningAnyRefusal()
    {
        var now = DateTimeOffset.UtcNow;
        using var harness = new LimitHarness(EnabledConfig(threshold: 1), () => now);
        await harness.RecordBadTranscodesAsync(AliceId, 5);

        Assert.False((await harness.AssessAsync(Request())).IsAdmitted);
        now = now.AddSeconds(1);
        Assert.False((await harness.AssessAsync(Request())).IsAdmitted);
        Assert.Single(harness.Messages.SentMessages);

        // A lull, then a deliberate retry, gets its own popup.
        now = now.AddSeconds(30);
        Assert.False((await harness.AssessAsync(Request())).IsAdmitted);
        Assert.Equal(2, harness.Messages.SentMessages.Count);
    }

    [Fact]
    public async Task ReReadsTheCountOnceItsShortLivedCacheExpires()
    {
        var now = DateTimeOffset.UtcNow;
        using var harness = new LimitHarness(EnabledConfig(threshold: 5), () => now);
        await harness.RecordBadTranscodesAsync(AliceId, 4);

        Assert.True((await harness.AssessAsync(Request())).IsAdmitted);

        await harness.RecordBadTranscodesAsync(AliceId, 1);

        // Still answering from the cached count.
        Assert.True((await harness.AssessAsync(Request())).IsAdmitted);

        now = now.AddSeconds(30);
        Assert.False((await harness.AssessAsync(Request())).IsAdmitted);
    }

    private static PluginConfiguration EnabledConfig(int threshold) => new()
    {
        EnableTranscodeLimit = true,
        TranscodeLimitThreshold = threshold,
        LoginNagTimeWindow = "Week"
    };

    private static TranscodeLimitRequest Request(
        Guid? userId = null,
        TranscodeReason reasons = TranscodeReason.VideoCodecNotSupported,
        bool isVideoRequest = true,
        bool isLiveStream = false,
        Guid? itemId = null)
    {
        return new TranscodeLimitRequest
        {
            IsVideoRequest = isVideoRequest,
            TranscodeReasons = reasons,
            IsLiveStream = isLiveStream,
            DeviceId = DeviceId,
            UserId = userId ?? AliceId,
            ItemId = itemId ?? MovieId,
            ItemName = "Movie"
        };
    }

    private sealed class LimitHarness : IDisposable
    {
        private readonly TestEventStore _store = new();
        private readonly SessionInfo _session = TestSessions.Create("session-1", DeviceId, AliceId);

        public LimitHarness(
            PluginConfiguration config,
            Func<DateTimeOffset>? clock = null,
            IClientMessageService? messages = null)
        {
            Messages = new RecordingClientMessageService();
            Messages.AddSession(_session);

            Guard = new TranscodeLimitGuard(
                _store.Store,
                messages ?? Messages,
                NullLogger<TranscodeLimitGuard>.Instance,
                () => config,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(3),
                clock ?? (() => DateTimeOffset.UtcNow));
        }

        public RecordingClientMessageService Messages { get; }

        public TranscodeLimitGuard Guard { get; }

        public Task<TranscodeLimitDecision> AssessAsync(TranscodeLimitRequest request)
            => Guard.AssessAsync(request, CancellationToken.None);

        public Task RecordBadTranscodesAsync(Guid userId, int count, DateTime? timestamp = null, string client = "Jellyfin Web")
            => _store.SeedBadTranscodesAsync(userId, count, timestamp, client);

        /// <summary>
        /// Puts the session into the state Jellyfin leaves it in once playback has been reported.
        /// </summary>
        /// <param name="itemId">The item the session is playing.</param>
        public void SetNowPlaying(Guid itemId)
        {
            _session.NowPlayingItem = new BaseItemDto { Id = itemId, Name = "Movie" };
        }

        public void Dispose() => _store.Dispose();
    }
}
