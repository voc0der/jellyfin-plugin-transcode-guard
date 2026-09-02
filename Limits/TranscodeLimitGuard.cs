using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using Jellyfin.Plugin.TranscodeGuard.Data;
using Jellyfin.Plugin.TranscodeGuard.Messaging;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeGuard.Limits;

/// <summary>
/// Turns the login nag's transcode count into an enforceable limit: once a user is at or over
/// <see cref="PluginConfiguration.TranscodeLimitThreshold"/>, their next transcode is refused
/// before FFmpeg is launched.
/// </summary>
/// <remarks>
/// <para>
/// The count, the window, and which transcodes count are all the login nag's, so an admin sets
/// one policy and picks two points on it: nag here, stop there. Anything the nag would ignore -
/// a bitrate-only transcode, an excluded user, a filtered client, Live TV when it is excluded -
/// is never refused either.
/// </para>
/// <para>
/// Every failure path allows the transcode. A limit that cannot be evaluated must not become an
/// outage.
/// </para>
/// </remarks>
public sealed class TranscodeLimitGuard
{
    // A refused client retries: jellyfin-web's playbackmanager falls back through progressively
    // stricter transcode options, and each hop is another StartFfMpeg, so answering from a few
    // seconds of memory keeps a retry storm off the event store's lock. A refusal records
    // nothing, so a refusing burst reads a count that genuinely cannot move. An admitted one can:
    // starts within the same window share a count that a preceding start is still on its way to
    // incrementing, so a user at the boundary can slip a small, bounded number of extra
    // transcodes through. That is the right trade for a limit whose input is a rolling weekly
    // count - it is not a licence check - and the same staleness is why an edited client filter
    // or time window takes effect within a window rather than instantly.
    private static readonly TimeSpan DefaultCountCacheLifetime = TimeSpan.FromSeconds(5);

    // Same burst, one popup. See GpuResourceGuard for the timing this is derived from.
    private static readonly TimeSpan DefaultNotificationQuietPeriod = TimeSpan.FromSeconds(3);

    private const string DefaultDeniedHeader = "Transcode limit reached";
    private const string DefaultDeniedMessage = "You have reached this server's transcoding limit. Switch to a client that can direct play to keep watching.";

    private readonly TranscodeEventStore _eventStore;
    private readonly IClientMessageService _clientMessageService;
    private readonly ILogger<TranscodeLimitGuard> _logger;
    private readonly Func<PluginConfiguration?> _configurationAccessor;
    private readonly TimeSpan _countCacheLifetime;
    private readonly TimeSpan _notificationQuietPeriod;
    private readonly Func<DateTimeOffset> _clock;

    private readonly Dictionary<string, CachedCount> _countCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastRefusalUtc = new(StringComparer.Ordinal);
    private readonly object _countCacheLock = new();
    private readonly object _suppressionLock = new();

    public TranscodeLimitGuard(
        TranscodeEventStore eventStore,
        IClientMessageService clientMessageService,
        ILogger<TranscodeLimitGuard> logger)
        : this(eventStore, clientMessageService, logger, () => Plugin.Instance?.Configuration)
    {
    }

    internal TranscodeLimitGuard(
        TranscodeEventStore eventStore,
        IClientMessageService clientMessageService,
        ILogger<TranscodeLimitGuard> logger,
        Func<PluginConfiguration?> configurationAccessor)
        : this(
            eventStore,
            clientMessageService,
            logger,
            configurationAccessor,
            DefaultCountCacheLifetime,
            DefaultNotificationQuietPeriod,
            () => DateTimeOffset.UtcNow)
    {
    }

    internal TranscodeLimitGuard(
        TranscodeEventStore eventStore,
        IClientMessageService clientMessageService,
        ILogger<TranscodeLimitGuard> logger,
        Func<PluginConfiguration?> configurationAccessor,
        TimeSpan countCacheLifetime,
        TimeSpan notificationQuietPeriod,
        Func<DateTimeOffset> clock)
    {
        _eventStore = eventStore;
        _clientMessageService = clientMessageService;
        _logger = logger;
        _configurationAccessor = configurationAccessor;
        _countCacheLifetime = countCacheLifetime;
        _notificationQuietPeriod = notificationQuietPeriod;
        _clock = clock;
    }

    /// <summary>
    /// Decides whether Jellyfin may launch this transcode, notifying the requesting client on refusal.
    /// </summary>
    /// <param name="request">The transcode Jellyfin is about to launch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decision, which carries the numbers behind a refusal.</returns>
    public async Task<TranscodeLimitDecision> AssessAsync(
        TranscodeLimitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var config = _configurationAccessor();
        if (config == null)
        {
            // The plugin is not fully loaded; never stand between Jellyfin and playback.
            return TranscodeLimitDecision.Allowed;
        }

        if (!config.EnableTranscodeLimit)
        {
            return TranscodeLimitDecision.Allowed;
        }

        // A limit of zero or less would refuse every transcode the moment the feature is switched
        // on, which is never what an admin means by a limit. Treat it as unconfigured.
        if (config.TranscodeLimitThreshold < 1)
        {
            if (ShouldNotify("invalid-threshold"))
            {
                _logger.LogWarning(
                    "Transcode limit is enabled but its threshold is {Threshold}; no transcode will be refused until it is set to 1 or more",
                    config.TranscodeLimitThreshold);
            }

            return TranscodeLimitDecision.Allowed;
        }

        // An unauthenticated request cannot be attributed to anyone's history.
        if (request.UserId == Guid.Empty)
        {
            return TranscodeLimitDecision.Allowed;
        }

        // Only what the login nag counts is refused: audio-only streams and bitrate-driven
        // transcodes never counted toward the limit, so they must not be stopped by it.
        if (!request.IsVideoRequest
            || !TranscodeGuardRules.MatchesConfiguredNagReasons(request.TranscodeReasons, config))
        {
            return TranscodeLimitDecision.Allowed;
        }

        if (TranscodeGuardRules.IsUserExcluded(request.UserId, config.ExcludedUserIds))
        {
            return TranscodeLimitDecision.Allowed;
        }

        // Live TV excluded from the nag means excluded from the limit: a channel that never
        // counted toward the total must not be the stream that gets refused because of it.
        if (config.ExcludeLiveTv && request.IsLiveStream)
        {
            return TranscodeLimitDecision.Allowed;
        }

        var session = TryResolveSession(request);

        // A filtered client's history never counted toward the total, so it must not be the
        // stream refused because of one. Only a positively identified filtered client is spared:
        // a request whose session cannot be correlated is still refused, or the limit would be
        // trivially avoidable.
        if (session != null && !TranscodeGuardRules.IsClientAllowed(session.Client, config))
        {
            return TranscodeLimitDecision.Allowed;
        }

        // A stream the user is already watching is a continuation, not a new transcode. Jellyfin
        // starts a fresh FFmpeg job for a seek, and the event recorded for this very playback is
        // what can push its owner over the limit - so without this, the movie that reached the
        // limit is the one cut off, mid-scene, and it cannot be resumed for the rest of the
        // window. The limit stops the next thing a user starts, never the thing they are watching.
        // The session only carries a now-playing item once playback has been reported, which is
        // exactly the case this must spare: a first launch has not reported one yet.
        if (request.ItemId != Guid.Empty && session?.NowPlayingItem?.Id == request.ItemId)
        {
            return TranscodeLimitDecision.Allowed;
        }

        var (days, timeWindowLabel) = TranscodeGuardRules.ResolveLoginNagWindow(config.LoginNagTimeWindow);
        var transcodeCount = await GetTranscodeCountAsync(request.UserId, days, config).ConfigureAwait(false);

        if (transcodeCount < config.TranscodeLimitThreshold)
        {
            return TranscodeLimitDecision.Allowed;
        }

        var decision = TranscodeLimitDecision.Denied(transcodeCount, config.TranscodeLimitThreshold, timeWindowLabel);

        // The decision is made. Everything past this point is notification and logging, and none
        // of it may reverse the refusal: an exception escaping here would reach the decorator's
        // fail-open catch and admit the very transcode we just refused. ClientMessageService does
        // not catch every failure Jellyfin's WebSocket send path can raise.
        try
        {
            await DenyAsync(request, session, config, decision, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BestEffort(() => _logger.LogError(
                ex,
                "Failed to notify the client about the blocked transcode of {ItemName}; the refusal still stands",
                request.ItemName ?? "Unknown"));
        }

        return decision;
    }

    /// <summary>
    /// Resolves the session behind a streaming request, treating a failure as "unknown session"
    /// rather than letting it escape. Session lookup is an input to the decision, so it must not
    /// be able to throw the decision away.
    /// </summary>
    /// <param name="request">The transcode being judged.</param>
    /// <returns>The session, or null when it cannot be identified.</returns>
    private SessionInfo? TryResolveSession(TranscodeLimitRequest request)
    {
        try
        {
            return _clientMessageService.ResolveSession(request.DeviceId, request.UserId, request.ItemId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BestEffort(() => _logger.LogDebug(
                ex,
                "Could not resolve the session behind the transcode of {ItemName}; judging it without one",
                request.ItemName ?? "Unknown"));
            return null;
        }
    }

    private async Task<int> GetTranscodeCountAsync(Guid userId, int days, PluginConfiguration config)
    {
        var cacheKey = string.Join(
            '|',
            userId.ToString("N", CultureInfo.InvariantCulture),
            days.ToString(CultureInfo.InvariantCulture));
        var now = _clock();

        lock (_countCacheLock)
        {
            if (_countCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresUtc > now)
            {
                return cached.Count;
            }
        }

        var status = await _eventStore.GetUserNagStatusAsync(
            userId.ToString(),
            days,
            storedEvent => TranscodeGuardRules.IsStoredEventAllowed(storedEvent, config)).ConfigureAwait(false);

        lock (_countCacheLock)
        {
            PruneExpiredCounts(now);
            _countCache[cacheKey] = new CachedCount(status.BadTranscodeCount, now + _countCacheLifetime);
        }

        return status.BadTranscodeCount;
    }

    private async Task DenyAsync(
        TranscodeLimitRequest request,
        SessionInfo? session,
        PluginConfiguration config,
        TranscodeLimitDecision decision,
        CancellationToken cancellationToken)
    {
        var notify = ShouldNotify(BuildSuppressionKey(request));

        if (!notify)
        {
            // Still refused - only the popup and the warning are de-duplicated.
            BestEffort(() => _logger.LogDebug(
                "Transcode blocked again for item {ItemName} within the notification suppression window",
                request.ItemName ?? "Unknown"));
            return;
        }

        BestEffort(() => _logger.LogWarning(
            "Transcode blocked for session {SessionId}, user {UserName}, item {ItemName}: {TranscodeCount} counted transcodes in the last {TimeWindow} is at or over the limit of {Threshold}; reasons {TranscodeReasons}",
            session?.Id ?? "Unknown",
            session?.UserName ?? request.UserId.ToString(),
            request.ItemName ?? "Unknown",
            decision.TranscodeCount,
            decision.TimeWindowLabel,
            decision.Threshold,
            request.TranscodeReasons));

        if (session == null)
        {
            BestEffort(() => _logger.LogDebug(
                "No session could be correlated to the blocked transcode of {ItemName}; the refusal still stands",
                request.ItemName ?? "Unknown"));
            return;
        }

        // Delivery is best effort. A client that cannot show a popup is still refused.
        await _clientMessageService.SendMessageAsync(
            session,
            new MessageCommand
            {
                // An admin who blanks these fields should still get a usable popup.
                Header = Fallback(config.TranscodeLimitHeader, DefaultDeniedHeader),
                Text = TranscodeGuardRules.FormatTranscodeLimitMessage(
                    Fallback(config.TranscodeLimitMessage, DefaultDeniedMessage),
                    decision.TranscodeCount,
                    decision.TimeWindowLabel,
                    decision.Threshold),
                TimeoutMs = config.MessageTimeoutMs
            },
            config.UseStickyTranscodeLimitMessages,
            "transcode limit block",
            $"{decision.TranscodeCount} counted transcodes against a limit of {decision.Threshold}",
            config.EnableLogging,
            _logger,
            cancellationToken).ConfigureAwait(false);
    }

    private static string Fallback(string? configured, string defaultText)
        => string.IsNullOrWhiteSpace(configured) ? defaultText : configured;

    /// <summary>
    /// Identifies "this client, this item" across renegotiation, matching the GPU guard so one
    /// press of play produces one popup however many times Jellyfin retries it.
    /// </summary>
    /// <param name="request">The refused transcode.</param>
    /// <returns>The suppression key.</returns>
    private static string BuildSuppressionKey(TranscodeLimitRequest request)
    {
        return string.Join(
            '|',
            request.DeviceId ?? string.Empty,
            request.ItemId.ToString("N", CultureInfo.InvariantCulture));
    }

    private bool ShouldNotify(string key)
    {
        var now = _clock();

        lock (_suppressionLock)
        {
            var notify = !_lastRefusalUtc.TryGetValue(key, out var previousRefusal)
                || now - previousRefusal >= _notificationQuietPeriod;

            // Recorded on every refusal, not just the announced ones: this measures the gap since
            // the last attempt, so an ongoing burst keeps extending and a lull always resets.
            PruneExpiredRefusals(now);
            _lastRefusalUtc[key] = now;

            return notify;
        }
    }

    private void PruneExpiredRefusals(DateTimeOffset now)
    {
        if (_lastRefusalUtc.Count == 0)
        {
            return;
        }

        var expired = _lastRefusalUtc
            .Where(entry => now - entry.Value >= _notificationQuietPeriod)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var key in expired)
        {
            _lastRefusalUtc.Remove(key);
        }
    }

    private void PruneExpiredCounts(DateTimeOffset now)
    {
        if (_countCache.Count == 0)
        {
            return;
        }

        var expired = _countCache
            .Where(entry => entry.Value.ExpiresUtc <= now)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var key in expired)
        {
            _countCache.Remove(key);
        }
    }

    private static void BestEffort(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A logger is diagnostic infrastructure. It must not change a refusal into an
            // allowance if a custom provider throws from ILogger.Log.
        }
    }

    private readonly struct CachedCount
    {
        internal CachedCount(int count, DateTimeOffset expiresUtc)
        {
            Count = count;
            ExpiresUtc = expiresUtc;
        }

        internal int Count { get; }

        internal DateTimeOffset ExpiresUtc { get; }
    }
}
