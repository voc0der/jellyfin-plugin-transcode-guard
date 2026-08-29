using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TranscodeNag.Data;
using Jellyfin.Plugin.TranscodeNag.Messaging;
using Jellyfin.Plugin.TranscodeNag.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeNag;

public class PlaybackMonitor : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly IClientMessageService _clientMessageService;
    private readonly ILogger<PlaybackMonitor> _logger;
    private readonly TranscodeEventStore _eventStore;
    private readonly HashSet<string> _naggedPlaybacks = new();

    // Notification state is tied to the SessionInfo instance rather than its ID. Jellyfin derives
    // session IDs from client/device identifiers, so a later session can reuse the same ID.
    private readonly HashSet<SessionInfo> _motdSentSessions = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<SessionInfo> _sessionNotificationsInProgress = new(ReferenceEqualityComparer.Instance);
    private readonly object _sessionNotificationLock = new();

    // "Open Jellyfin" detection for long-lived sessions:
    // We poll session activity timestamps (via reflection) and treat a large activity jump as a new "open".
    // This keeps behavior compatible across Jellyfin builds even if the SessionInfo property name changes.
    private readonly Dictionary<SessionInfo, DateTime> _sessionLastActivityUtc = new(ReferenceEqualityComparer.Instance);
    private readonly object _sessionLastActivityLock = new();
    private Timer? _sessionPollTimer;

    // If a session goes idle for at least this long and then becomes active again,
    // treat it as the user "opening" Jellyfin again.
    private static readonly TimeSpan OpenIdleThreshold = TimeSpan.FromMinutes(10);

    // Upper bound on how long the login nag waits behind a freshly sent MOTD.
    private const int MaxMotdFollowUpDelayMs = 30000;

    public PlaybackMonitor(
        ISessionManager sessionManager,
        IClientMessageService clientMessageService,
        IApplicationPaths applicationPaths,
        ILogger<PlaybackMonitor> logger,
        ILogger<TranscodeEventStore> eventStoreLogger)
    {
        _sessionManager = sessionManager;
        _clientMessageService = clientMessageService;
        _logger = logger;
        _eventStore = new TranscodeEventStore(applicationPaths, eventStoreLogger);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sessionManager.SessionControllerConnected += OnSessionControllerConnected;
        _sessionManager.SessionEnded += OnSessionEnded;
        // Polling is used ONLY to catch re-opens of existing sessions.
        // (SessionControllerConnected covers fresh sessions once messages can be delivered.)
        _sessionPollTimer = new Timer(PollSessionsForReopen, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
        _logger.LogInformation("PlaybackMonitor started - listening for playback and session events");

        // The guard is wired into Jellyfin's transcode manager at service-registration time, long
        // before any logger exists. Surface a failed hookup here so it is not silent.
        var decorationFailure = PluginServiceRegistrator.DecorationFailure;
        if (decorationFailure != null)
        {
            _logger.LogWarning(
                "GPU resource guard is not installed and will never refuse a transcode on this server ({Reason}). All other Transcode Nag features are unaffected.",
                decorationFailure);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.SessionControllerConnected -= OnSessionControllerConnected;
        _sessionManager.SessionEnded -= OnSessionEnded;
        _sessionPollTimer?.Dispose();
        _logger.LogInformation("PlaybackMonitor stopped");
        return Task.CompletedTask;
    }

    private void PollSessionsForReopen(object? state)
    {
        if (Plugin.Instance == null)
        {
            return;
        }

        var config = Plugin.Instance.Configuration;
        if (!config.EnableLoginNag)
        {
            return;
        }

        // If we can't read a last-activity timestamp, polling can't safely infer "reopen".
        // In that case, the controller-connected event still works for fresh sessions.
        foreach (var session in _sessionManager.Sessions)
        {
            if (session.Id == null || session.UserId == Guid.Empty)
            {
                continue;
            }

            var lastActivity = TryGetSessionLastActivityUtc(session);
            if (!lastActivity.HasValue)
            {
                continue;
            }

            var shouldTreatAsOpen = false;

            lock (_sessionLastActivityLock)
            {
                if (_sessionLastActivityUtc.TryGetValue(session, out var prev))
                {
                    // If the session jumped forward by a lot, consider it a "re-open".
                    if (lastActivity.Value > prev && (lastActivity.Value - prev) >= OpenIdleThreshold)
                    {
                        shouldTreatAsOpen = true;
                    }

                    _sessionLastActivityUtc[session] = lastActivity.Value;
                }
                else
                {
                    // First time seeing this session in the poller - treat as open.
                    _sessionLastActivityUtc[session] = lastActivity.Value;
                    shouldTreatAsOpen = true;
                }
            }

            if (shouldTreatAsOpen)
            {
                lock (_sessionNotificationLock)
                {
                    // A fresh-session notification may be waiting for the MOTD to expire.
                    // Let that sequence deliver the login nag so polling cannot overtake it.
                    if (_sessionNotificationsInProgress.Contains(session))
                    {
                        continue;
                    }
                }

                _ = MaybeSendLoginOrOpenNagAsync(session, config);
            }
        }
    }

    private static DateTime? TryGetSessionLastActivityUtc(SessionInfo session)
    {
        try
        {
            // Jellyfin SessionInfo commonly exposes LastActivityDate (DateTime) or LastActivityDateUtc.
            var type = session.GetType();
            var prop = type.GetProperty("LastActivityDate", BindingFlags.Instance | BindingFlags.Public)
                       ?? type.GetProperty("LastActivityDateUtc", BindingFlags.Instance | BindingFlags.Public)
                       ?? type.GetProperty("LastActivity", BindingFlags.Instance | BindingFlags.Public);

            if (prop == null)
            {
                return null;
            }

            var value = prop.GetValue(session);
            if (value is DateTime dt)
            {
                return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            }

            return null;
        }
        catch (AmbiguousMatchException)
        {
            return null;
        }
        catch (TargetException)
        {
            return null;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
        catch (MethodAccessException)
        {
            return null;
        }
    }

    private async void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (Plugin.Instance == null || e.Session == null)
            {
                return;
            }

            var config = Plugin.Instance.Configuration;

            // Wait for transcoding info to be available
            await Task.Delay(config.DelaySeconds * 1000).ConfigureAwait(false);

            // Re-fetch session to get updated transcoding info
            var session = _sessionManager.Sessions.FirstOrDefault(s => s.Id == e.Session.Id);
            if (session == null || session.NowPlayingItem == null)
            {
                return;
            }

            var playbackKey = $"{session.Id}_{session.NowPlayingItem.Id}";

            if (!IsItemAllowed(session, config, "playback nag"))
            {
                _naggedPlaybacks.Remove(playbackKey);
                return;
            }

            if (!IsClientAllowed(session, config, "playback nag"))
            {
                _naggedPlaybacks.Remove(playbackKey);
                return;
            }

            var transcodeInfo = session.TranscodingInfo;
            if (transcodeInfo == null || transcodeInfo.IsVideoDirect)
            {
                // Good playback (direct play / direct stream) - record a credit so users don't get dinged
                // on login/open nags until the next bad transcode.
                RecordImprovementCreditIfNeeded(session, config);

                // Not transcoding - remove from nagged list if present
                _naggedPlaybacks.Remove(playbackKey);
                return;
            }

            // Check if transcoding is due to unsupported format/codec
            if (TranscodeNagRules.ShouldNagTranscode(transcodeInfo, config))
            {
                // Record the event
                RecordTranscodeEvent(session, transcodeInfo);

                if (!_naggedPlaybacks.Contains(playbackKey)
                    && await SendNagMessageAsync(session, config).ConfigureAwait(false))
                {
                    _naggedPlaybacks.Add(playbackKey);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error handling playback-start nag");
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Session?.Id != null && e.Item?.Id != null)
        {
            var playbackKey = $"{e.Session.Id}_{e.Item.Id}";
            _naggedPlaybacks.Remove(playbackKey);
        }
    }

    private bool IsItemAllowed(SessionInfo session, Configuration.PluginConfiguration config, string context)
    {
        if (TranscodeNagRules.IsItemAllowed(session.NowPlayingItem, config))
        {
            return true;
        }

        if (config.EnableLogging)
        {
            _logger.LogInformation(
                "Skipping {Context} for excluded Live TV item {ItemName} ({ItemId}) on session {SessionId}",
                context,
                session.NowPlayingItem?.Name ?? "Unknown",
                session.NowPlayingItem?.Id.ToString() ?? "Unknown",
                session.Id ?? "Unknown");
        }

        return false;
    }

    private bool IsClientAllowed(SessionInfo session, Configuration.PluginConfiguration config, string context)
    {
        if (TranscodeNagRules.IsClientAllowed(session.Client, config))
        {
            return true;
        }

        if (config.EnableLogging)
        {
            _logger.LogInformation(
                "Skipping {Context} for filtered client {Client} on session {SessionId}",
                context,
                session.Client ?? "Unknown",
                session.Id ?? "Unknown");
        }

        return false;
    }

    private static string ResolveNagMessage(SessionInfo session, Configuration.PluginConfiguration config)
    {
        var transcodeInfo = session.TranscodingInfo;
        var overrides = config.ReasonMessageOverrides;

        if (transcodeInfo == null || overrides == null || overrides.Length == 0 || config.AlertTranscodeReasons == null)
        {
            return config.NagMessage;
        }

        var activeReasons = transcodeInfo.TranscodeReasons;

        // First override configured for an active reason wins; reason order sets the priority.
        var overrideMatch = config.AlertTranscodeReasons
            .Where(reasonName => !string.IsNullOrWhiteSpace(reasonName))
            .Where(reasonName => Enum.TryParse<TranscodeReason>(reasonName, true, out var parsedReason)
                && (activeReasons & parsedReason) != 0)
            .SelectMany(reasonName => overrides.Where(entry => entry != null
                && string.Equals(entry.ReasonName, reasonName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.Message)))
            .FirstOrDefault();

        return overrideMatch?.Message ?? config.NagMessage;
    }

    private async Task<bool> SendNagMessageAsync(SessionInfo session, Configuration.PluginConfiguration config)
    {
        if (session.Id == null)
        {
            return false;
        }

        // Check if user is excluded from nag messages
        if (TranscodeNagRules.IsUserExcluded(session.UserId, config.ExcludedUserIds))
        {
            if (config.EnableLogging)
            {
                _logger.LogInformation(
                    "Skipping nag for excluded user {UserId} ({UserName})",
                    session.UserId,
                    session.UserName ?? "Unknown");
            }
            return false;
        }

        var transcodeReasons = session.TranscodingInfo?.TranscodeReasons.ToString() ?? "Unknown";

        return await SendMessageCommandWithDiagnosticsAsync(
            session,
            config,
            new MessageCommand
            {
                Header = "Transcoding Detected",
                Text = ResolveNagMessage(session, config),
                TimeoutMs = config.MessageTimeoutMs
            },
            "playback nag",
            $"Reasons: {transcodeReasons}").ConfigureAwait(false);
    }

    private Task<bool> SendMessageCommandWithDiagnosticsAsync(
        SessionInfo session,
        Configuration.PluginConfiguration config,
        MessageCommand command,
        string context,
        string detail)
    {
        // The logger is passed through so these diagnostics keep appearing under PlaybackMonitor's
        // category rather than moving to the shared service.
        return _clientMessageService.SendMessageAsync(
            session,
            command,
            context,
            detail,
            config.EnableLogging,
            _logger,
            CancellationToken.None);
    }

    private void RecordTranscodeEvent(SessionInfo session, TranscodingInfo transcodeInfo)
    {
        if (session.UserId == Guid.Empty || session.NowPlayingItem == null)
        {
            return;
        }

        var transcodeEvent = new TranscodeEvent
        {
            UserId = session.UserId.ToString(),
            UserName = session.UserName ?? "Unknown",
            ItemId = session.NowPlayingItem.Id.ToString(),
            ItemName = session.NowPlayingItem.Name ?? "Unknown",
            Timestamp = DateTime.UtcNow,
            Reasons = transcodeInfo.TranscodeReasons,
            Client = session.Client ?? "Unknown",
            IsLiveTv = TranscodeNagRules.IsLiveTvItem(session.NowPlayingItem),
            Kind = NagEventKind.BadTranscode
        };

        _eventStore.AddEvent(transcodeEvent);
    }

    private void RecordImprovementCreditIfNeeded(SessionInfo session, Configuration.PluginConfiguration config)
    {
        if (session.UserId == Guid.Empty || session.NowPlayingItem == null)
        {
            return;
        }

        // Only record a credit if the user has had at least one bad transcode recently.
        // This keeps events.json from growing rapidly for users who already direct play everything.
        try
        {
            // Fire-and-forget: GetUserNagStatusAsync takes a lock and reads the file.
            _ = Task.Run(async () =>
            {
                var status = await _eventStore.GetUserNagStatusAsync(
                    session.UserId.ToString(),
                    30,
                    e => TranscodeNagRules.IsStoredEventAllowed(e, config)).ConfigureAwait(false);

                if (!status.LastBadTranscodeUtc.HasValue)
                {
                    return;
                }

                // If they already have an improvement credit after their most recent bad transcode, don't add another.
                if (status.HasImprovementCredit)
                {
                    return;
                }

                var creditEvent = new TranscodeEvent
                {
                    UserId = session.UserId.ToString(),
                    UserName = session.UserName ?? "Unknown",
                    ItemId = session.NowPlayingItem.Id.ToString(),
                    ItemName = session.NowPlayingItem.Name ?? "Unknown",
                    Timestamp = DateTime.UtcNow,
                    Reasons = 0,
                    Client = session.Client ?? "Unknown",
                    IsLiveTv = TranscodeNagRules.IsLiveTvItem(session.NowPlayingItem),
                    Kind = NagEventKind.ImprovementCredit
                };

                _eventStore.AddEvent(creditEvent);
            });
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "Skipping improvement credit task because monitor is disposing");
        }
        catch (TaskSchedulerException ex)
        {
            _logger.LogDebug(ex, "Unable to queue improvement credit task");
        }
    }

    private async void OnSessionControllerConnected(object? sender, SessionEventArgs e)
    {
        if (Plugin.Instance == null
            || e.SessionInfo is not { Id: not null } session
            || session.UserId == Guid.Empty)
        {
            return;
        }

        var config = Plugin.Instance.Configuration;

        if (!config.EnableLoginNag && !config.EnableMotd)
        {
            return;
        }

        // More than one controller can connect to a session at nearly the same time. Run one
        // notification sequence at a time so the login nag cannot overtake a pending MOTD.
        lock (_sessionNotificationLock)
        {
            if (!_sessionNotificationsInProgress.Add(session))
            {
                return;
            }
        }

        // Seed the reopen tracker now. Otherwise its first poll can classify this fresh session
        // as a reopen and send the login nag while the MOTD is still visible.
        var lastActivity = TryGetSessionLastActivityUtc(session);
        if (lastActivity.HasValue)
        {
            lock (_sessionLastActivityLock)
            {
                _sessionLastActivityUtc[session] = lastActivity.Value;
            }
        }

        try
        {
            var motdSent = false;

            // Keep MOTD failures from suppressing the login nag, and never let this
            // async void handler throw into the host.
            try
            {
                motdSent = await MaybeSendMotdAsync(session, config).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Error handling session-connect MOTD");
            }

            try
            {
                // Many clients only render one message at a time. If we just sent the MOTD,
                // let it expire before the login nag replaces it - otherwise the nag is never
                // seen but is still recorded as sent, suppressing it for the whole window.
                if (motdSent && config.EnableLoginNag)
                {
                    // Clamped because MessageTimeoutMs is only range-checked by the config UI.
                    await Task.Delay(Math.Clamp(config.MessageTimeoutMs, 0, MaxMotdFollowUpDelayMs)).ConfigureAwait(false);
                }

                // A session can end (and its deterministic ID can be reused) while waiting for
                // the MOTD to expire. Only continue for the same live SessionInfo instance.
                var currentSession = _sessionManager.Sessions.FirstOrDefault(candidate => ReferenceEquals(candidate, session));
                if (currentSession != null)
                {
                    await MaybeSendLoginOrOpenNagAsync(currentSession, config).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Error handling session-connect login nag");
            }
        }
        finally
        {
            lock (_sessionNotificationLock)
            {
                _sessionNotificationsInProgress.Remove(session);
            }
        }
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        var session = e.SessionInfo;
        if (session == null)
        {
            return;
        }

        lock (_sessionNotificationLock)
        {
            _motdSentSessions.Remove(session);
            _sessionNotificationsInProgress.Remove(session);
        }

        lock (_sessionLastActivityLock)
        {
            _sessionLastActivityUtc.Remove(session);
        }
    }

    /// <summary>
    /// Sends the MOTD if this session qualifies. Returns true only when a message was actually delivered.
    /// </summary>
    private async Task<bool> MaybeSendMotdAsync(SessionInfo session, Configuration.PluginConfiguration config)
    {
        if (session.Id == null || session.UserId == Guid.Empty)
        {
            return false;
        }

        if (!config.EnableMotd || string.IsNullOrWhiteSpace(config.MotdMessage))
        {
            return false;
        }

        if (TranscodeNagRules.IsUserExcluded(session.UserId, config.MotdExcludedUserIds))
        {
            if (config.EnableLogging)
            {
                _logger.LogInformation(
                    "Skipping MOTD for excluded user {UserId} ({UserName})",
                    session.UserId,
                    session.UserName ?? "Unknown");
            }

            return false;
        }

        if (!TranscodeNagRules.IsMotdClientAllowed(session.Client, config))
        {
            if (config.EnableLogging)
            {
                _logger.LogInformation(
                    "Skipping MOTD for filtered client {Client} on session {SessionId}",
                    session.Client ?? "Unknown",
                    session.Id);
            }

            return false;
        }

        // Claim the session before sending so concurrent controller connections cannot double-send.
        lock (_sessionNotificationLock)
        {
            if (!_motdSentSessions.Add(session))
            {
                return false;
            }
        }

        var sent = false;
        try
        {
            sent = await SendMessageCommandWithDiagnosticsAsync(
                session,
                config,
                new MessageCommand
                {
                    Header = "Message of the Day",
                    Text = config.MotdMessage,
                    TimeoutMs = config.MessageTimeoutMs
                },
                "motd",
                "Message of the day").ConfigureAwait(false);
        }
        finally
        {
            // Release the claim on any failure (including an exception the send helper does not catch)
            // so the MOTD can still be delivered if the session reconnects.
            if (!sent)
            {
                lock (_sessionNotificationLock)
                {
                    _motdSentSessions.Remove(session);
                }
            }
        }

        return sent;
    }

    private async Task MaybeSendLoginOrOpenNagAsync(SessionInfo session, Configuration.PluginConfiguration config)
    {
        if (session.Id == null || session.UserId == Guid.Empty)
        {
            return;
        }

        if (!config.EnableLoginNag)
        {
            return;
        }

        // Check if user is excluded from nag messages
        if (TranscodeNagRules.IsUserExcluded(session.UserId, config.ExcludedUserIds))
        {
            if (config.EnableLogging)
            {
                _logger.LogInformation(
                    "Skipping login nag for excluded user {UserId}",
                    session.UserId);
            }
            return;
        }

        if (!IsClientAllowed(session, config, "login/open nag"))
        {
            return;
        }

        var userId = session.UserId.ToString();

        var (days, timeWindowText) = TranscodeNagRules.ResolveLoginNagWindow(config.LoginNagTimeWindow);

        var status = await _eventStore.GetUserNagStatusAsync(
            userId,
            days,
            e => TranscodeNagRules.IsStoredEventAllowed(e, config)).ConfigureAwait(false);

        // Rate limit: only once per configured period.
        if (status.NaggedRecently)
        {
            return;
        }

        // If they demonstrated improvement (a direct play/stream) after their last bad transcode,
        // don't ding them again until they regress with another bad transcode.
        if (status.HasImprovementCredit)
        {
            return;
        }

        if (status.BadTranscodeCount < config.LoginNagThreshold)
        {
            return;
        }

        var message = TranscodeNagRules.FormatLoginNagMessage(
            config.LoginNagMessage,
            status.BadTranscodeCount,
            timeWindowText);

        var sent = await SendMessageCommandWithDiagnosticsAsync(
            session,
            config,
            new MessageCommand
            {
                Header = "Transcoding Alert",
                Text = message,
                TimeoutMs = config.MessageTimeoutMs
            },
            "login/open nag",
            $"{status.BadTranscodeCount} bad transcodes in last {timeWindowText}").ConfigureAwait(false);

        if (!sent)
        {
            return;
        }

        // Persist the rate-limit marker.
        _eventStore.AddEvent(new TranscodeEvent
        {
            UserId = userId,
            UserName = session.UserName ?? "Unknown",
            ItemId = session.NowPlayingItem?.Id.ToString() ?? string.Empty,
            ItemName = session.NowPlayingItem?.Name ?? string.Empty,
            Timestamp = DateTime.UtcNow,
            Reasons = 0,
            Client = session.Client ?? "Unknown",
            Kind = NagEventKind.NagSent
        });
    }
}
