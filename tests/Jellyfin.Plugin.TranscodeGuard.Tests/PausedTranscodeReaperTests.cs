using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using Jellyfin.Plugin.TranscodeGuard.Sessions;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

public class PausedTranscodeTrackerTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Evaluate_IgnoresATranscodeThatIsPlaying()
    {
        var tracker = new PausedTranscodeTracker();
        var session = PausedTranscode();
        session.PlayState.IsPaused = false;
        var config = EnabledConfig();

        tracker.Evaluate(new[] { session }, config, Start);

        Assert.Empty(tracker.Evaluate(new[] { session }, config, Start.AddHours(3)));
        Assert.Equal(0, tracker.TrackedCount);
    }

    [Fact]
    public void Evaluate_IgnoresPausedDirectPlayByDefault()
    {
        var tracker = new PausedTranscodeTracker();
        var session = PausedDirectPlay();
        var config = EnabledConfig();

        tracker.Evaluate(new[] { session }, config, Start);

        Assert.Empty(tracker.Evaluate(new[] { session }, config, Start.AddHours(3)));
        Assert.Equal(0, tracker.TrackedCount);
    }

    [Fact]
    public void Evaluate_StopsPausedDirectPlayWhenTheAdminOptsIn()
    {
        var tracker = new PausedTranscodeTracker();
        var session = PausedDirectPlay();
        var config = EnabledConfig();
        config.ReapPausedDirectPlay = true;
        config.PausedTranscodeWarningMinutes = 0;

        tracker.Evaluate(new[] { session }, config, Start);
        var verdicts = tracker.Evaluate(new[] { session }, config, Start.AddMinutes(25));

        Assert.Equal(PausedTranscodeAction.Stop, Assert.Single(verdicts).Action);
    }

    [Fact]
    public void Evaluate_NeverKillsDirectPlayBecauseThereIsNoJobToEnd()
    {
        var tracker = new PausedTranscodeTracker();
        var session = PausedDirectPlay();
        var config = EnabledConfig();
        config.ReapPausedDirectPlay = true;
        config.PausedTranscodeWarningMinutes = 0;

        tracker.Evaluate(new[] { session }, config, Start);
        Assert.Single(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(25)));

        // The client ignored the stop, but there is no FFmpeg process to take away.
        Assert.Empty(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(26)));
    }

    [Fact]
    public void Evaluate_LeavesDirectPlayAloneWhenNothingIsPlaying()
    {
        var tracker = new PausedTranscodeTracker();
        var session = PausedDirectPlay();

        // A paused flag that outlived its playback is not something to stop.
        session.NowPlayingItem = null;
        var config = EnabledConfig();
        config.ReapPausedDirectPlay = true;

        tracker.Evaluate(new[] { session }, config, Start);

        Assert.Empty(tracker.Evaluate(new[] { session }, config, Start.AddHours(3)));
    }

    [Fact]
    public void Evaluate_StillKillsTranscodesWhenDirectPlayIsInScope()
    {
        var tracker = new PausedTranscodeTracker();
        var session = PausedTranscode();
        var config = EnabledConfig();
        config.ReapPausedDirectPlay = true;
        config.PausedTranscodeWarningMinutes = 0;

        tracker.Evaluate(new[] { session }, config, Start);
        Assert.Single(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(25)));

        var kill = Assert.Single(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(26)));
        Assert.Equal(PausedTranscodeAction.Kill, kill.Action);
    }

    [Fact]
    public void Evaluate_ReapsAPausedDirectStream()
    {
        var tracker = new PausedTranscodeTracker();
        var session = PausedTranscode();

        // A remux still owns an FFmpeg process and its output files.
        session.TranscodingInfo!.IsVideoDirect = true;
        var config = EnabledConfig();

        tracker.Evaluate(new[] { session }, config, Start);
        var verdicts = tracker.Evaluate(new[] { session }, config, Start.AddMinutes(25));

        Assert.Equal(PausedTranscodeAction.Stop, Assert.Single(verdicts).Action);
    }

    [Fact]
    public void Evaluate_ReapsPausedLiveTvEvenWhenNagsExcludeIt()
    {
        var tracker = new PausedTranscodeTracker();
        var session = PausedTranscode();
        session.NowPlayingItem = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "BBC One",
            Type = BaseItemKind.TvChannel,
            IsLive = true
        };

        // The Live TV exclusion governs who gets messaged, not which processes may sit idle.
        var config = EnabledConfig();
        config.ExcludeLiveTv = true;

        tracker.Evaluate(new[] { session }, config, Start);
        var verdicts = tracker.Evaluate(new[] { session }, config, Start.AddMinutes(25));

        Assert.Equal(PausedTranscodeAction.Stop, Assert.Single(verdicts).Action);
    }

    [Fact]
    public void Evaluate_WarnsThenStopsThenKills()
    {
        var tracker = new PausedTranscodeTracker();
        var session = PausedTranscode();
        var config = EnabledConfig();

        Assert.Empty(tracker.Evaluate(new[] { session }, config, Start));
        Assert.Empty(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(22)));

        var warning = Assert.Single(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(23)));
        Assert.Equal(PausedTranscodeAction.Warn, warning.Action);
        Assert.Equal(2, warning.MinutesUntilStop);

        // Only one warning per pause, however many ticks fall inside the lead time.
        Assert.Empty(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(24)));

        var stop = Assert.Single(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(25)));
        Assert.Equal(PausedTranscodeAction.Stop, stop.Action);

        // The client is given the grace period to act on the stop before it is taken away.
        Assert.Empty(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(25).AddSeconds(5)));

        var kill = Assert.Single(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(25).AddSeconds(15)));
        Assert.Equal(PausedTranscodeAction.Kill, kill.Action);
    }

    [Fact]
    public void Evaluate_DoesNotKillAClientThatObeyedTheStop()
    {
        var tracker = new PausedTranscodeTracker();
        var session = PausedTranscode();
        var config = EnabledConfig();

        tracker.Evaluate(new[] { session }, config, Start);
        Assert.Single(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(25)));

        // Playback stopped: Jellyfin has already torn the job down.
        session.TranscodingInfo = null;
        session.PlayState.PlayMethod = null;
        session.NowPlayingItem = null;

        Assert.Empty(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(26)));
        Assert.Equal(0, tracker.TrackedCount);
    }

    [Fact]
    public void Evaluate_RestartsTheClockWhenPlaybackResumes()
    {
        var tracker = new PausedTranscodeTracker();
        var session = PausedTranscode();
        var config = EnabledConfig();
        config.PausedTranscodeWarningMinutes = 0;

        tracker.Evaluate(new[] { session }, config, Start);

        session.PlayState.IsPaused = false;
        Assert.Empty(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(20)));

        session.PlayState.IsPaused = true;
        Assert.Empty(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(21)));

        // 25 minutes after resuming, not 25 minutes after the first pause.
        Assert.Empty(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(45)));
        Assert.Equal(
            PausedTranscodeAction.Stop,
            Assert.Single(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(46))).Action);
    }

    [Fact]
    public void Evaluate_RestartsTheClockWhenTheViewerSeeksWhilePaused()
    {
        var tracker = new PausedTranscodeTracker();
        var session = PausedTranscode();
        var config = EnabledConfig();
        config.PausedTranscodeWarningMinutes = 0;

        tracker.Evaluate(new[] { session }, config, Start);

        // Someone scrubbing the timeline is still there, even without pressing play.
        session.PlayState.PositionTicks = 90_000_000_000;
        Assert.Empty(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(24)));
        Assert.Empty(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(48)));
        Assert.Equal(
            PausedTranscodeAction.Stop,
            Assert.Single(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(49))).Action);
    }

    [Fact]
    public void Evaluate_SkipsExcludedUsers()
    {
        var tracker = new PausedTranscodeTracker();
        var excluded = PausedTranscode();
        var reaped = PausedTranscode();
        var config = EnabledConfig();
        config.PausedTranscodeExcludedUserIds = new[] { excluded.UserId.ToString("N") };

        var sessions = new[] { excluded, reaped };
        tracker.Evaluate(sessions, config, Start);
        var verdicts = tracker.Evaluate(sessions, config, Start.AddMinutes(25));

        Assert.Same(reaped, Assert.Single(verdicts).Session);
    }

    [Fact]
    public void Evaluate_ForgetsSessionsThatWentAway()
    {
        var tracker = new PausedTranscodeTracker();
        var first = PausedTranscode();
        var second = PausedTranscode();
        var config = EnabledConfig();

        tracker.Evaluate(new[] { first, second }, config, Start);
        Assert.Equal(2, tracker.TrackedCount);

        tracker.Evaluate(new[] { second }, config, Start.AddMinutes(1));
        Assert.Equal(1, tracker.TrackedCount);
    }

    [Fact]
    public void Evaluate_ClampsAHandEditedTimeout()
    {
        var tracker = new PausedTranscodeTracker();
        var session = PausedTranscode();
        var config = EnabledConfig();

        // The config page range-checks these fields; a hand-edited XML file does not.
        config.PausedTranscodeTimeoutMinutes = 0;
        config.PausedTranscodeWarningMinutes = 0;

        tracker.Evaluate(new[] { session }, config, Start);

        Assert.Empty(tracker.Evaluate(new[] { session }, config, Start.AddSeconds(30)));
        Assert.Single(tracker.Evaluate(new[] { session }, config, Start.AddMinutes(1)));
    }

    [Theory]
    [InlineData(25, 2, 2)]
    [InlineData(25, -5, 0)]
    [InlineData(5, 90, 4)]
    [InlineData(1, 1, 0)]
    public void ResolveWarningMinutes_KeepsTheLeadInsideTheTimeout(int timeout, int warning, int expected)
    {
        var config = new PluginConfiguration
        {
            PausedTranscodeTimeoutMinutes = timeout,
            PausedTranscodeWarningMinutes = warning
        };

        Assert.Equal(expected, PausedTranscodeTracker.ResolveWarningMinutes(config));
    }

    [Fact]
    public void FormatWarningMessage_SubstitutesTheRemainingMinutes()
    {
        Assert.Equal(
            "Stopping in 2 minute(s).",
            PausedTranscodeTracker.FormatWarningMessage("Stopping in {{minutes}} minute(s).", 2));
    }

    internal static PluginConfiguration EnabledConfig()
        => new()
        {
            EnablePausedTranscodeReaper = true,
            PausedTranscodeTimeoutMinutes = 25,
            PausedTranscodeWarningMinutes = 2
        };

    internal static SessionInfo PausedDirectPlay()
    {
        var session = PausedTranscode();
        session.TranscodingInfo = null;
        session.PlayState.PlayMethod = PlayMethod.DirectPlay;

        return session;
    }

    internal static SessionInfo PausedTranscode()
    {
        var session = TestSessions.Create(
            "session-" + Guid.NewGuid().ToString("N"),
            "device-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid());

        session.NowPlayingItem = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Blade Runner 2049",
            Type = BaseItemKind.Movie
        };
        session.TranscodingInfo = new TranscodingInfo
        {
            VideoCodec = "h264",
            AudioCodec = "aac",
            TranscodeReasons = TranscodeReason.VideoCodecNotSupported
        };
        session.PlayState.IsPaused = true;
        session.PlayState.PlayMethod = PlayMethod.Transcode;
        session.PlayState.PositionTicks = 12_000_000_000;

        return session;
    }
}

public class PausedTranscodeReaperTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RunOnceAsync_DoesNothingWhileTheFeatureIsOff()
    {
        var session = PausedTranscodeTrackerTests.PausedTranscode();
        var harness = new Harness(session);
        var config = PausedTranscodeTrackerTests.EnabledConfig();
        config.EnablePausedTranscodeReaper = false;

        await harness.Reaper.RunOnceAsync(config, Start);
        await harness.Reaper.RunOnceAsync(config, Start.AddHours(4));

        Assert.Empty(harness.Stopped);
        Assert.Empty(harness.Killed);
        Assert.Empty(harness.Messages.SentMessages);
    }

    [Fact]
    public async Task RunOnceAsync_WarnsBeforeStopping()
    {
        var session = PausedTranscodeTrackerTests.PausedTranscode();
        var harness = new Harness(session);
        var config = PausedTranscodeTrackerTests.EnabledConfig();
        config.PausedTranscodeWarningMessage = "Stopping in {{minutes}} minute(s).";

        await harness.Reaper.RunOnceAsync(config, Start);
        await harness.Reaper.RunOnceAsync(config, Start.AddMinutes(23));

        var sent = Assert.Single(harness.Messages.SentMessages);
        Assert.Equal("Stopping in 2 minute(s).", sent.Command.Text);
        Assert.Empty(harness.Stopped);
    }

    [Fact]
    public async Task RunOnceAsync_AsksTheClientToStopBeforeEndingTheJob()
    {
        var session = PausedTranscodeTrackerTests.PausedTranscode();
        var harness = new Harness(session);
        var config = PausedTranscodeTrackerTests.EnabledConfig();
        config.PausedTranscodeWarningMinutes = 0;

        await harness.Reaper.RunOnceAsync(config, Start);
        await harness.Reaper.RunOnceAsync(config, Start.AddMinutes(25));

        Assert.Same(session, Assert.Single(harness.Stopped));
        Assert.Empty(harness.Killed);
    }

    [Fact]
    public async Task RunOnceAsync_EndsTheJobOfAClientThatIgnoredTheStop()
    {
        var session = PausedTranscodeTrackerTests.PausedTranscode();
        var harness = new Harness(session);
        var config = PausedTranscodeTrackerTests.EnabledConfig();
        config.PausedTranscodeWarningMinutes = 0;

        await harness.Reaper.RunOnceAsync(config, Start);
        await harness.Reaper.RunOnceAsync(config, Start.AddMinutes(25));
        await harness.Reaper.RunOnceAsync(config, Start.AddMinutes(26));

        Assert.Equal(session.DeviceId, Assert.Single(harness.Killed));
    }

    [Fact]
    public async Task RunOnceAsync_StillStopsClientsWhenTheTranscodeManagerIsUnavailable()
    {
        var session = PausedTranscodeTrackerTests.PausedTranscode();
        var harness = new Harness(session, transcodeKillerAvailable: false);
        var config = PausedTranscodeTrackerTests.EnabledConfig();
        config.PausedTranscodeWarningMinutes = 0;

        await harness.Reaper.RunOnceAsync(config, Start);
        await harness.Reaper.RunOnceAsync(config, Start.AddMinutes(25));
        await harness.Reaper.RunOnceAsync(config, Start.AddMinutes(26));

        Assert.Single(harness.Stopped);
    }

    [Fact]
    public async Task RunOnceAsync_KeepsGoingWhenOneSessionFails()
    {
        var failing = PausedTranscodeTrackerTests.PausedTranscode();
        var healthy = PausedTranscodeTrackerTests.PausedTranscode();
        var harness = new Harness(failing, healthy);
        harness.FailStopFor = failing;
        var config = PausedTranscodeTrackerTests.EnabledConfig();
        config.PausedTranscodeWarningMinutes = 0;

        await harness.Reaper.RunOnceAsync(config, Start);
        await harness.Reaper.RunOnceAsync(config, Start.AddMinutes(25));

        Assert.Same(healthy, Assert.Single(harness.Stopped));
    }

    [Fact]
    public async Task HostedServiceLifecycleIsSafeToRepeat()
    {
        var harness = new Harness(PausedTranscodeTrackerTests.PausedTranscode());

        await harness.Reaper.StartAsync(CancellationToken.None);
        await harness.Reaper.StopAsync(CancellationToken.None);

        // Jellyfin disposes hosted services after stopping them, and a plugin reload can repeat it.
        harness.Reaper.Dispose();
        harness.Reaper.Dispose();

        Assert.Empty(harness.Stopped);
    }

    private sealed class Harness
    {
        internal Harness(params SessionInfo[] sessions)
            : this(true, sessions)
        {
        }

        internal Harness(SessionInfo session, bool transcodeKillerAvailable)
            : this(transcodeKillerAvailable, session)
        {
        }

        private Harness(bool transcodeKillerAvailable, params SessionInfo[] sessions)
        {
            Messages = new RecordingClientMessageService();

            Reaper = new PausedTranscodeReaper(
                () => sessions,
                (session, _) =>
                {
                    if (ReferenceEquals(session, FailStopFor))
                    {
                        throw new InvalidOperationException("session is gone");
                    }

                    Stopped.Add(session);
                    return Task.CompletedTask;
                },
                transcodeKillerAvailable
                    ? deviceId =>
                    {
                        Killed.Add(deviceId);
                        return Task.CompletedTask;
                    }
                    : null,
                Messages,
                NullLogger<PausedTranscodeReaper>.Instance);
        }

        internal PausedTranscodeReaper Reaper { get; }

        internal RecordingClientMessageService Messages { get; }

        internal List<SessionInfo> Stopped { get; } = new();

        internal List<string> Killed { get; } = new();

        internal SessionInfo? FailStopFor { get; set; }
    }
}
