using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TranscodeGuard.Browser;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using Jellyfin.Plugin.TranscodeGuard.Models;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

[Collection(PlaybackMonitorCollection.Name)]
public sealed class PlaybackMonitorTests : IAsyncLifetime
{
    private readonly Plugin? _previousPlugin = Plugin.Instance;
    private readonly string _pluginPath = Path.Combine(Path.GetTempPath(), "transcode-guard-monitor", Guid.NewGuid().ToString("N"));
    private readonly TestEventStore _events = new();
    private readonly PlaybackMessages _messages = new();
    private readonly ListLogger<PlaybackMonitor> _logger = new();
    private readonly PlaybackSessionManager _sessions;
    private readonly PlaybackMonitor _monitor;
    private readonly PluginConfiguration _config;
    private readonly SessionInfo _session;

    public PlaybackMonitorTests()
    {
        var manager = DispatchProxy.Create<ISessionManager, PlaybackSessionManager>();
        _sessions = (PlaybackSessionManager)manager;
        _monitor = new PlaybackMonitor(manager, _messages, _events.Store, _logger);
        var plugin = new Plugin(new TestApplicationPaths(_pluginPath), new PlaybackConfigurationSerializer());
        _config = plugin.Configuration;
        _config.DelaySeconds = 0;
        _config.EnableLoginNag = false;
        _config.MessageTimeoutMs = 0;
        _session = TestSessions.Create("session", "device", Guid.NewGuid());
        _session.LastActivityDate = DateTime.UtcNow;
        _session.NowPlayingItem = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movie", Type = BaseItemKind.Movie };
        _session.TranscodingInfo = new TranscodingInfo { TranscodeReasons = TranscodeReason.VideoCodecNotSupported };
        _sessions.Sessions.Add(_session);
    }

    public async ValueTask InitializeAsync() => await _monitor.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _monitor.StopAsync(CancellationToken.None);
        SetPlugin(_previousPlugin);
        _events.Dispose();
        if (Directory.Exists(_pluginPath))
        {
            Directory.Delete(_pluginPath, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_UnsubscribesEveryHandler()
    {
        Assert.Equal(4, _sessions.SubscriberCount);

        await _monitor.StopAsync(TestContext.Current.CancellationToken);
        await StartPlaybackAsync();
        await ConnectAsync();

        Assert.Equal(0, _sessions.SubscriberCount);
        Assert.Empty(_messages.Deliveries);
        Assert.Empty(await UserEventsAsync());
    }

    [Fact]
    public async Task Events_IgnoreAnUnavailablePlugin()
    {
        SetPlugin(null);

        await StartPlaybackAsync();
        await ConnectAsync();
        PollSessions();

        Assert.Empty(_messages.Deliveries);
        Assert.Empty(await UserEventsAsync());
    }

    [Fact]
    public async Task Events_IgnoreMissingSessions()
    {
        await _sessions.RaiseAsync(nameof(ISessionManager.PlaybackStart), new PlaybackProgressEventArgs { Session = null! });
        await _sessions.RaiseAsync(nameof(ISessionManager.PlaybackStopped), new PlaybackStopEventArgs { Session = null! });
        await _sessions.RaiseAsync(nameof(ISessionManager.SessionControllerConnected), new SessionEventArgs { SessionInfo = null! });
        await _sessions.RaiseAsync(nameof(ISessionManager.SessionEnded), new SessionEventArgs { SessionInfo = null! });

        Assert.Empty(_messages.Deliveries);
        Assert.Empty(_messages.Cancellations);
    }

    [Fact]
    public async Task Playback_RecordsTheTranscodeAndSendsTheConfiguredWarning()
    {
        _config.NagMessage = "Use a compatible client.";
        _config.MessageTimeoutMs = 4321;
        _config.UseStickyPlaybackMessages = true;

        await StartPlaybackAsync();

        var delivery = Assert.Single(_messages.Deliveries);
        Assert.Same(_session, delivery.Session);
        Assert.Equal("Transcoding Detected", delivery.Command.Header);
        Assert.Equal(_config.NagMessage, delivery.Command.Text);
        Assert.Equal(4321, delivery.Command.TimeoutMs);
        Assert.True(delivery.Sticky);
        Assert.Null(delivery.Arguments);
        var recorded = Assert.Single(await UserEventsAsync());
        Assert.Equal(NagEventKind.BadTranscode, recorded.Kind);
        Assert.Equal(_session.NowPlayingItem!.Id.ToString(), recorded.ItemId);
        Assert.Equal("Movie", recorded.ItemName);
        Assert.Equal(_session.Client, recorded.Client);
        Assert.Equal(TranscodeReason.VideoCodecNotSupported, recorded.Reasons);
        Assert.False(recorded.IsLiveTv);
    }

    [Fact]
    public async Task Playback_UsesTheCurrentSessionInsteadOfTheEventSnapshot()
    {
        var stale = TestSessions.Create(_session.Id, _session.DeviceId, _session.UserId);

        await _sessions.RaiseAsync(nameof(ISessionManager.PlaybackStart), new PlaybackProgressEventArgs { Session = stale });

        Assert.Same(_session, Assert.Single(_messages.Deliveries).Session);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Playback_IgnoresEndedPlayback(bool sessionEnded)
    {
        if (sessionEnded)
        {
            _sessions.Sessions.Clear();
        }
        else
        {
            _session.NowPlayingItem = null;
        }

        await StartPlaybackAsync();

        Assert.Empty(_messages.Deliveries);
        Assert.Empty(await UserEventsAsync());
    }

    [Fact]
    public async Task Playback_WarnsOnceUntilPlaybackStops()
    {
        await StartPlaybackAsync();
        await StartPlaybackAsync();
        Assert.Single(_messages.Deliveries);

        await _sessions.RaiseAsync(nameof(ISessionManager.PlaybackStopped), new PlaybackStopEventArgs
        {
            Session = _session,
            Item = new Movie { Id = _session.NowPlayingItem!.Id }
        });
        await StartPlaybackAsync();

        Assert.Equal(2, _messages.Deliveries.Count);
        Assert.Equal((_session, "playback nag"), Assert.Single(_messages.Cancellations));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Playback_RetriesAfterFailedDelivery(bool throws)
    {
        _messages.Send = _ => throws ? throw new IOException("disconnected") : Task.FromResult(false);
        await StartPlaybackAsync();

        _messages.Send = _ => Task.FromResult(true);
        await StartPlaybackAsync();
        await StartPlaybackAsync();

        Assert.Equal(2, _messages.Deliveries.Count);
        Assert.Equal(throws, _logger.Entries.Any(entry => entry.Level == LogLevel.Error));
    }

    [Theory]
    [InlineData("live tv")]
    [InlineData("excluded client")]
    [InlineData("client outside allowlist")]
    [InlineData("bitrate")]
    public async Task Playback_FilteredTranscodesProduceNeitherWarningsNorHistory(string filter)
    {
        switch (filter)
        {
            case "live tv":
                _config.ExcludeLiveTv = true;
                _session.NowPlayingItem!.Type = BaseItemKind.TvChannel;
                break;
            case "excluded client":
                _config.ExcludedClientPatterns = new[] { "Web" };
                break;
            case "client outside allowlist":
                _config.IncludedClientPatterns = new[] { "Android" };
                break;
            case "bitrate":
                _session.TranscodingInfo!.TranscodeReasons = TranscodeReason.ContainerBitrateExceedsLimit;
                break;
        }

        await StartPlaybackAsync();

        Assert.Empty(_messages.Deliveries);
        Assert.Empty(await UserEventsAsync());
    }

    [Fact]
    public async Task Playback_ExcludedUsersAreNotMessaged()
    {
        _config.ExcludedUserIds = new[] { _session.UserId.ToString() };

        await StartPlaybackAsync();

        Assert.Empty(_messages.Deliveries);
        Assert.Contains(_logger.Entries, entry => entry.Message.Contains("Skipping nag for excluded user", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Playback_UsesConfiguredReasonPriorityForOverrides()
    {
        _session.TranscodingInfo!.TranscodeReasons |= TranscodeReason.AudioCodecNotSupported;
        _config.AlertTranscodeReasons = new[] { "", "invalid", "AudioCodecNotSupported", "VideoCodecNotSupported" };
        _config.ReasonMessageOverrides = new[]
        {
            null!,
            new ReasonMessageOverride { ReasonName = "VideoCodecNotSupported", Message = "Video warning" },
            new ReasonMessageOverride { ReasonName = "AudioCodecNotSupported", Message = " " },
            new ReasonMessageOverride { ReasonName = "audiocodecnotsupported", Message = "Audio warning" }
        };

        await StartPlaybackAsync();

        Assert.Equal("Audio warning", Assert.Single(_messages.Deliveries).Command.Text);
    }

    [Fact]
    public async Task Playback_UnmatchedOverrideFallsBackToTheDefaultMessage()
    {
        _config.ReasonMessageOverrides = new[]
        {
            new ReasonMessageOverride { ReasonName = "AudioCodecNotSupported", Message = "Audio warning" }
        };

        await StartPlaybackAsync();

        Assert.Equal(_config.NagMessage, Assert.Single(_messages.Deliveries).Command.Text);
    }

    [Theory]
    [InlineData("Jellyfin Web", true)]
    [InlineData("Jellyfin Media Player", false)]
    public async Task Playback_AttachesTheInstallWarningOnlyForBrowserClients(string client, bool isBrowser)
    {
        _config.EnableBrowserInstallNag = true;
        _config.BrowserInstallUrl = "https://example.org/client";
        _session.Client = client;

        await StartPlaybackAsync();

        var delivery = Assert.Single(_messages.Deliveries);
        if (isBrowser)
        {
            Assert.NotNull(delivery.Arguments);
            Assert.Equal(BrowserNagRules.MarkerValue, delivery.Arguments[BrowserNagRules.MarkerArgument]);
            Assert.Equal(_config.BrowserInstallUrl, delivery.Arguments[BrowserNagRules.InstallUrlArgument]);
            Assert.Equal("Video codec not supported", delivery.Arguments[BrowserNagRules.ReasonArgument]);
            Assert.True(Guid.TryParseExact(delivery.Arguments[BrowserNagRules.NagIdArgument], "N", out _));
        }
        else
        {
            Assert.Null(delivery.Arguments);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Playback_DirectPlaybackCreditsImprovementAfterABadTranscode(bool directStream)
    {
        await _events.SeedBadTranscodesAsync(_session.UserId, 1);
        _session.TranscodingInfo = directStream ? new TranscodingInfo { IsVideoDirect = true } : null;

        await StartPlaybackAsync();
        await WaitForAsync(async () => (await UserEventsAsync()).Any(entry => entry.Kind == NagEventKind.ImprovementCredit));

        Assert.Empty(_messages.Deliveries);
        var credit = Assert.Single(await UserEventsAsync(), entry => entry.Kind == NagEventKind.ImprovementCredit);
        Assert.Equal(_session.NowPlayingItem!.Id.ToString(), credit.ItemId);
        Assert.Equal((TranscodeReason)0, credit.Reasons);
        Assert.True((await _events.Store.GetUserNagStatusAsync(_session.UserId.ToString(), 30)).HasImprovementCredit);
    }

    [Fact]
    public async Task Motd_SendsOncePerSessionAndCancelsOnSessionEnd()
    {
        _config.EnableMotd = true;
        _config.MotdMessage = "Maintenance tonight.";
        _config.UseStickyMotdMessages = true;
        _config.MessageTimeoutMs = 8765;

        await ConnectAsync();
        await ConnectAsync();

        var delivery = Assert.Single(_messages.Deliveries);
        Assert.Equal("Message of the Day", delivery.Command.Header);
        Assert.Equal(_config.MotdMessage, delivery.Command.Text);
        Assert.Equal(8765, delivery.Command.TimeoutMs);
        Assert.True(delivery.Sticky);

        await EndSessionAsync();
        await ConnectAsync();

        Assert.Equal(2, _messages.Deliveries.Count);
        Assert.Equal((_session, (string?)null), Assert.Single(_messages.Cancellations));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("blank message")]
    [InlineData("excluded user")]
    [InlineData("excluded client")]
    [InlineData("client outside allowlist")]
    [InlineData("anonymous")]
    [InlineData("missing session id")]
    public async Task Motd_SkipsIneligibleSessions(string reason)
    {
        _config.EnableMotd = true;
        switch (reason)
        {
            case "disabled": _config.EnableMotd = false; break;
            case "blank message": _config.MotdMessage = " "; break;
            case "excluded user": _config.MotdExcludedUserIds = new[] { _session.UserId.ToString() }; break;
            case "excluded client": _config.MotdExcludedClientPatterns = new[] { "Web" }; break;
            case "client outside allowlist": _config.MotdIncludedClientPatterns = new[] { "Android" }; break;
            case "anonymous": _session.UserId = Guid.Empty; break;
            case "missing session id": _session.Id = null!; break;
        }

        await ConnectAsync();

        Assert.Empty(_messages.Deliveries);
    }

    [Fact]
    public async Task Motd_UsesItsOwnExclusions()
    {
        _config.EnableMotd = true;
        _config.ExcludedUserIds = new[] { _session.UserId.ToString() };
        _config.ExcludedClientPatterns = new[] { "Web" };

        await ConnectAsync();

        Assert.Equal("motd", Assert.Single(_messages.Deliveries).Context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Motd_RetriesAfterFailedDelivery(bool throws)
    {
        _config.EnableMotd = true;
        _messages.Send = _ => throws ? throw new IOException("disconnected") : Task.FromResult(false);
        await ConnectAsync();

        _messages.Send = _ => Task.FromResult(true);
        await ConnectAsync();
        await ConnectAsync();

        Assert.Equal(2, _messages.Deliveries.Count);
        Assert.Equal(throws, _logger.Entries.Any(entry => entry.Level == LogLevel.Error));
    }

    [Fact]
    public async Task Motd_AReplacementSessionWithTheSameIdGetsItsOwnMessage()
    {
        _config.EnableMotd = true;
        await ConnectAsync();
        var replacement = TestSessions.Create(_session.Id, _session.DeviceId, _session.UserId);
        _sessions.Sessions.Clear();
        _sessions.Sessions.Add(replacement);

        await _sessions.RaiseAsync(nameof(ISessionManager.SessionControllerConnected), new SessionEventArgs { SessionInfo = replacement });

        Assert.Equal(2, _messages.Deliveries.Count);
        Assert.Same(replacement, _messages.Deliveries[1].Session);
    }

    [Theory]
    [InlineData("Week", "week")]
    [InlineData("Month", "month")]
    public async Task Login_FormatsTheWarningAndPersistsTheRateLimit(string window, string expectedWindow)
    {
        _config.EnableLoginNag = true;
        _config.LoginNagThreshold = 2;
        _config.LoginNagTimeWindow = window;
        _config.LoginNagMessage = "{{transcodes}} transcodes in the last {{timewindow}}.";
        _config.UseStickyLoginNagMessages = true;
        _config.MessageTimeoutMs = 6789;
        _session.NowPlayingItem = null;
        await _events.SeedBadTranscodesAsync(_session.UserId, 2);

        await ConnectAsync();

        var delivery = Assert.Single(_messages.Deliveries);
        Assert.Equal("Transcoding Alert", delivery.Command.Header);
        Assert.Equal($"2 transcodes in the last {expectedWindow}.", delivery.Command.Text);
        Assert.Equal(6789, delivery.Command.TimeoutMs);
        Assert.True(delivery.Sticky);
        var marker = Assert.Single(await UserEventsAsync(), entry => entry.Kind == NagEventKind.NagSent);
        Assert.Equal(string.Empty, marker.ItemId);
        Assert.Equal(string.Empty, marker.ItemName);

        await ConnectAsync();

        Assert.Single(_messages.Deliveries);
    }

    [Theory]
    [InlineData("below threshold")]
    [InlineData("excluded user")]
    [InlineData("excluded client")]
    [InlineData("old history")]
    [InlineData("excluded live tv history")]
    [InlineData("excluded client history")]
    [InlineData("improvement credit")]
    [InlineData("recent nag")]
    public async Task Login_DoesNotNagIneligibleUsers(string reason)
    {
        _config.EnableLoginNag = true;
        _config.LoginNagThreshold = 2;
        await _events.SeedBadTranscodesAsync(
            _session.UserId,
            reason == "below threshold" ? 1 : 2,
            reason == "old history" ? DateTime.UtcNow.AddDays(-8) : null,
            client: reason == "excluded client history" ? "Filtered client" : "Jellyfin Web",
            isLiveTv: reason == "excluded live tv history");
        switch (reason)
        {
            case "excluded user": _config.ExcludedUserIds = new[] { _session.UserId.ToString() }; break;
            case "excluded client": _config.ExcludedClientPatterns = new[] { "Web" }; break;
            case "excluded live tv history": _config.ExcludeLiveTv = true; break;
            case "excluded client history": _config.ExcludedClientPatterns = new[] { "Filtered" }; break;
            case "improvement credit": await AddMarkerAsync(NagEventKind.ImprovementCredit); break;
            case "recent nag": await AddMarkerAsync(NagEventKind.NagSent); break;
        }

        await ConnectAsync();

        Assert.Empty(_messages.Deliveries);
    }

    [Fact]
    public async Task Login_MonthWindowIncludesHistoryOutsideTheWeek()
    {
        _config.EnableLoginNag = true;
        _config.LoginNagThreshold = 1;
        _config.LoginNagTimeWindow = "Month";
        await _events.SeedBadTranscodesAsync(_session.UserId, 1, DateTime.UtcNow.AddDays(-8));

        await ConnectAsync();

        Assert.Equal("login/open nag", Assert.Single(_messages.Deliveries).Context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Login_FailedDeliveryDoesNotConsumeTheRateLimit(bool throws)
    {
        _config.EnableLoginNag = true;
        _config.LoginNagThreshold = 1;
        await _events.SeedBadTranscodesAsync(_session.UserId, 1);
        _messages.Send = _ => throws ? throw new IOException("disconnected") : Task.FromResult(false);

        await ConnectAsync();

        Assert.DoesNotContain(await UserEventsAsync(), entry => entry.Kind == NagEventKind.NagSent);
        _messages.Send = _ => Task.FromResult(true);
        await ConnectAsync();

        Assert.Equal(2, _messages.Deliveries.Count);
        Assert.Single(await UserEventsAsync(), entry => entry.Kind == NagEventKind.NagSent);
        Assert.Equal(throws, _logger.Entries.Any(entry => entry.Level == LogLevel.Error));
    }

    [Fact]
    public async Task Connection_OverlappingControllersAndPollingCannotOvertakeTheMotd()
    {
        _config.EnableMotd = true;
        _config.EnableLoginNag = true;
        _config.LoginNagThreshold = 1;
        await _events.SeedBadTranscodesAsync(_session.UserId, 1);
        var motdDelivery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _messages.Send = delivery => delivery.Context == "motd" ? motdDelivery.Task : Task.FromResult(true);

        var firstConnection = ConnectAsync();
        try
        {
            await ConnectAsync();
            _session.LastActivityDate = _session.LastActivityDate.AddMinutes(10);
            PollSessions();

            Assert.Equal("motd", Assert.Single(_messages.Deliveries).Context);
            Assert.False(firstConnection.IsCompleted);
        }
        finally
        {
            motdDelivery.TrySetResult(true);
            await firstConnection;
        }

        Assert.Equal(new[] { "motd", "login/open nag" }, _messages.Deliveries.Select(delivery => delivery.Context));
        Assert.Single(await UserEventsAsync(), entry => entry.Kind == NagEventKind.NagSent);
    }

    [Fact]
    public async Task Connection_MotdFailureDoesNotSuppressTheLoginNag()
    {
        _config.EnableMotd = true;
        _config.EnableLoginNag = true;
        _config.LoginNagThreshold = 1;
        await _events.SeedBadTranscodesAsync(_session.UserId, 1);
        _messages.Send = delivery => delivery.Context == "motd" ? throw new IOException("disconnected") : Task.FromResult(true);

        await ConnectAsync();

        Assert.Equal(new[] { "motd", "login/open nag" }, _messages.Deliveries.Select(delivery => delivery.Context));
        Assert.Single(await UserEventsAsync(), entry => entry.Kind == NagEventKind.NagSent);
    }

    [Fact]
    public async Task Connection_DoesNotSendTheFollowUpToAReplacementSession()
    {
        _config.EnableMotd = true;
        _config.EnableLoginNag = true;
        _config.LoginNagThreshold = 1;
        await _events.SeedBadTranscodesAsync(_session.UserId, 1);
        var motdDelivery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _messages.Send = _ => motdDelivery.Task;

        var connection = ConnectAsync();
        try
        {
            await EndSessionAsync();
            _sessions.Sessions.Clear();
            _sessions.Sessions.Add(TestSessions.Create(_session.Id, _session.DeviceId, _session.UserId));
        }
        finally
        {
            motdDelivery.TrySetResult(true);
            await connection;
        }

        Assert.Equal("motd", Assert.Single(_messages.Deliveries).Context);
        Assert.DoesNotContain(await UserEventsAsync(), entry => entry.Kind == NagEventKind.NagSent);
    }

    [Fact]
    public async Task Poll_FirstObservationCanNagAnExistingSession()
    {
        _config.EnableLoginNag = true;
        _config.LoginNagThreshold = 1;
        await _events.SeedBadTranscodesAsync(_session.UserId, 1);

        PollSessions();
        await WaitForAsync(async () => (await UserEventsAsync()).Any(entry => entry.Kind == NagEventKind.NagSent));

        Assert.Equal("login/open nag", Assert.Single(_messages.Deliveries).Context);
    }

    [Fact]
    public async Task Poll_OnlyNagsAgainAfterAnActivityJumpOfAtLeastTenMinutes()
    {
        _config.EnableLoginNag = true;
        _config.LoginNagThreshold = 1;
        await ConnectAsync();
        await _events.SeedBadTranscodesAsync(_session.UserId, 1);

        PollSessions();
        _session.LastActivityDate = _session.LastActivityDate.AddMinutes(9);
        PollSessions();
        Assert.Empty(_messages.Deliveries);

        _session.LastActivityDate = _session.LastActivityDate.AddMinutes(10);
        PollSessions();
        await WaitForAsync(async () => (await UserEventsAsync()).Any(entry => entry.Kind == NagEventKind.NagSent));

        Assert.Equal("login/open nag", Assert.Single(_messages.Deliveries).Context);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("anonymous")]
    [InlineData("missing session id")]
    public async Task Poll_IgnoresIneligibleSessions(string reason)
    {
        _config.EnableLoginNag = reason != "disabled";
        _config.LoginNagThreshold = 1;
        await _events.SeedBadTranscodesAsync(_session.UserId, 1);
        if (reason == "anonymous")
        {
            _session.UserId = Guid.Empty;
        }
        else if (reason == "missing session id")
        {
            _session.Id = null!;
        }

        PollSessions();

        Assert.Empty(_messages.Deliveries);
    }

    private async Task AddMarkerAsync(NagEventKind kind)
    {
        _events.Store.AddEvent(new TranscodeEvent
        {
            UserId = _session.UserId.ToString(),
            Kind = kind,
            Timestamp = DateTime.UtcNow,
            Client = _session.Client
        });
        Assert.Single(await UserEventsAsync(), entry => entry.Kind == kind);
    }

    private Task StartPlaybackAsync()
        => _sessions.RaiseAsync(nameof(ISessionManager.PlaybackStart), new PlaybackProgressEventArgs { Session = _session });

    private Task ConnectAsync()
        => _sessions.RaiseAsync(nameof(ISessionManager.SessionControllerConnected), new SessionEventArgs { SessionInfo = _session });

    private Task EndSessionAsync()
        => _sessions.RaiseAsync(nameof(ISessionManager.SessionEnded), new SessionEventArgs { SessionInfo = _session });

    private Task<List<TranscodeEvent>> UserEventsAsync()
        => _events.Store.GetUserEventsAsync(_session.UserId.ToString(), 30);

    // Drive the timer callback explicitly; tests never wait for the production polling interval.
    private void PollSessions()
        => typeof(PlaybackMonitor).GetMethod("PollSessionsForReopen", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_monitor, new object?[] { null });

    private static void SetPlugin(Plugin? plugin)
        => typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, plugin);

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!await condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
