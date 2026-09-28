using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TranscodeGuard.Browser;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using Jellyfin.Plugin.TranscodeGuard.Gpu;
using Jellyfin.Plugin.TranscodeGuard.Messaging;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeGuard.Limits;

/// <summary>
/// Caps how many video transcodes one user can run at the same time: once a user's other devices
/// are at their maximum, the next transcode is refused before FFmpeg is launched.
/// </summary>
/// <remarks>
/// <para>
/// The count is the FFmpeg jobs this guard has seen admitted and not yet exited, not Jellyfin's
/// session list. A session's transcoding info appears seconds after launch, which would let a
/// quick second press of play through, while a job is known from the moment it is admitted.
/// </para>
/// <para>
/// It is counted per device. A device plays one thing at a time, so its own jobs never count
/// against it: a seek, a track change or the next episode replaces that device's transcode rather
/// than adding one, and cannot be refused unless the user's other devices are over the limit.
/// </para>
/// <para>
/// This is a resource cap, not a nag. Every video transcode counts, bitrate-only and Live TV
/// included, and the nag's user and client exclusions do not apply: a per-user maximum is how a
/// user gets more room. Direct play, remux and audio-only streams never count and are never
/// refused. Every failure path allows the transcode.
/// </para>
/// </remarks>
public sealed class ConcurrentTranscodeGuard
{
    // Same burst, one popup. See GpuResourceGuard for the timing this is derived from.
    private static readonly TimeSpan DefaultNotificationQuietPeriod = TimeSpan.FromSeconds(3);

    private const string DefaultDeniedHeader = "Too many transcodes at once";
    private const string DefaultDeniedMessage = "You're already transcoding on other devices, the most this server allows at a time. Stop one, or switch to a client that can direct play, to watch this.";

    private readonly IClientMessageService _clientMessageService;
    private readonly ILogger<ConcurrentTranscodeGuard> _logger;
    private readonly Func<PluginConfiguration?> _configurationAccessor;
    private readonly TimeSpan _notificationQuietPeriod;
    private readonly Func<DateTimeOffset> _clock;

    private readonly List<ConcurrentTranscodeSlot> _slots = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRefusalUtc = new(StringComparer.Ordinal);
    private readonly object _slotLock = new();
    private readonly object _suppressionLock = new();

    public ConcurrentTranscodeGuard(
        IClientMessageService clientMessageService,
        ILogger<ConcurrentTranscodeGuard> logger)
        : this(clientMessageService, logger, () => Plugin.Instance?.Configuration)
    {
    }

    internal ConcurrentTranscodeGuard(
        IClientMessageService clientMessageService,
        ILogger<ConcurrentTranscodeGuard> logger,
        Func<PluginConfiguration?> configurationAccessor)
        : this(
            clientMessageService,
            logger,
            configurationAccessor,
            DefaultNotificationQuietPeriod,
            () => DateTimeOffset.UtcNow)
    {
    }

    internal ConcurrentTranscodeGuard(
        IClientMessageService clientMessageService,
        ILogger<ConcurrentTranscodeGuard> logger,
        Func<PluginConfiguration?> configurationAccessor,
        TimeSpan notificationQuietPeriod,
        Func<DateTimeOffset> clock)
    {
        _clientMessageService = clientMessageService;
        _logger = logger;
        _configurationAccessor = configurationAccessor;
        _notificationQuietPeriod = notificationQuietPeriod;
        _clock = clock;
    }

    /// <summary>
    /// Decides whether Jellyfin may launch this transcode, notifying the requesting client on refusal.
    /// An admitted video transcode's decision carries a slot the caller must either mark launched
    /// or dispose.
    /// </summary>
    /// <param name="request">The transcode Jellyfin is about to launch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decision, which carries the numbers behind a refusal.</returns>
    public async Task<ConcurrentTranscodeDecision> AssessAsync(
        TranscodeLimitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Remux and audio-only streams also launch FFmpeg, but neither encodes video.
        if (!request.IsVideoRequest || GpuAdmissionPolicy.IsCopyCodec(request.OutputVideoCodec))
        {
            return ConcurrentTranscodeDecision.Allowed;
        }

        // An unauthenticated request cannot be attributed to anyone's count.
        if (request.UserId == Guid.Empty)
        {
            return ConcurrentTranscodeDecision.Allowed;
        }

        // Counted even while the limit is off or unset, so switching it on counts the transcodes
        // that are already running instead of letting everyone start a fresh set.
        var config = _configurationAccessor();
        var limit = config is { EnableConcurrentTranscodeLimit: true } ? ResolveLimit(config, request.UserId) : 0;
        var deviceKey = BuildDeviceKey(request);

        int activeTranscodes;
        lock (_slotLock)
        {
            _slots.RemoveAll(slot => !IsLive(slot));
            activeTranscodes = CountOtherDevices(request.UserId, deviceKey);

            if (limit < 1 || activeTranscodes < limit)
            {
                var slot = new ConcurrentTranscodeSlot(this, request.UserId, deviceKey);
                _slots.Add(slot);
                return ConcurrentTranscodeDecision.Admitted(slot);
            }
        }

        var decision = ConcurrentTranscodeDecision.Denied(activeTranscodes, limit);

        // The decision is made. Nothing past this point may reverse it: an exception escaping here
        // would reach the decorator's fail-open catch and admit the transcode just refused.
        try
        {
            await DenyAsync(request, config!, decision, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BestEffort(() => _logger.LogError(
                ex,
                "Failed to notify the client about the refused simultaneous transcode of {ItemName}; the refusal still stands",
                request.ItemName ?? "Unknown"));
        }

        return decision;
    }

    /// <summary>
    /// Resolves the maximum that applies to a user: their own entry wins, higher or lower, and the
    /// global maximum covers everyone without one.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="userId">The user.</param>
    /// <returns>The maximum, or 0 when the user has none.</returns>
    internal static int ResolveLimit(PluginConfiguration config, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(config);

        foreach (var entry in config.UserConcurrentTranscodeLimits ?? Array.Empty<UserConcurrentTranscodeLimit>())
        {
            // A per-user value below 1 would refuse every transcode, which is never what an admin
            // means by a maximum, so it reads as "no entry" and the global maximum applies.
            // Both "N" and dashed GUIDs are accepted so hand-edited configs still work.
            if (entry is { MaxTranscodes: >= 1 }
                && Guid.TryParse(entry.UserId?.Trim(), out var entryUserId)
                && entryUserId == userId)
            {
                return entry.MaxTranscodes;
            }
        }

        return Math.Max(0, config.MaxConcurrentTranscodes);
    }

    internal void CompleteSlot(ConcurrentTranscodeSlot slot, TranscodingJob? launchedJob)
    {
        lock (_slotLock)
        {
            if (launchedJob == null)
            {
                _slots.Remove(slot);
            }
            else
            {
                slot.Job = launchedJob;
            }
        }
    }

    private int CountOtherDevices(Guid userId, string deviceKey)
    {
        return _slots
            .Where(slot => slot.UserId == userId
                && !string.Equals(slot.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase))
            .Select(slot => slot.DeviceKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    /// <summary>
    /// Identifies the device a transcode plays on. Without a device ID the play session stands in;
    /// without either, the transcode is its own device, so it is counted but excuses nothing else.
    /// </summary>
    /// <param name="request">The transcode being judged.</param>
    /// <returns>The device key.</returns>
    private static string BuildDeviceKey(TranscodeLimitRequest request)
    {
        if (!string.IsNullOrEmpty(request.DeviceId))
        {
            return "device|" + request.DeviceId;
        }

        if (!string.IsNullOrEmpty(request.PlaySessionId))
        {
            return "play|" + request.PlaySessionId;
        }

        return "job|" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }

    private static bool IsLive(ConcurrentTranscodeSlot slot)
    {
        var job = slot.Job;

        // Admitted and not launched yet: it holds its place, or the next start could take it.
        if (job == null)
        {
            return true;
        }

        if (job.HasExited)
        {
            return false;
        }

        try
        {
            return job.Process?.HasExited != true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A disposed Process throws here. It is gone either way, and a slot that could never
            // expire would hold the user's place until the server restarts.
            return false;
        }
    }

    private async Task DenyAsync(
        TranscodeLimitRequest request,
        PluginConfiguration config,
        ConcurrentTranscodeDecision decision,
        CancellationToken cancellationToken)
    {
        if (!ShouldNotify(BuildSuppressionKey(request)))
        {
            // Still refused - only the popup and the warning are de-duplicated.
            BestEffort(() => _logger.LogDebug(
                "Simultaneous transcode refused again for item {ItemName} within the notification suppression window",
                request.ItemName ?? "Unknown"));
            return;
        }

        var session = _clientMessageService.ResolveSession(request.DeviceId, request.UserId, request.ItemId);

        BestEffort(() => _logger.LogWarning(
            "Simultaneous transcode refused for session {SessionId}, user {UserName}, item {ItemName}: {ActiveTranscodes} other device(s) already transcoding is at or over the limit of {Limit}",
            session?.Id ?? "Unknown",
            session?.UserName ?? request.UserId.ToString(),
            request.ItemName ?? "Unknown",
            decision.ActiveTranscodes,
            decision.Limit));

        if (session == null)
        {
            BestEffort(() => _logger.LogDebug(
                "No session could be correlated to the refused simultaneous transcode of {ItemName}; the refusal still stands",
                request.ItemName ?? "Unknown"));
            return;
        }

        // An admin who blanks these fields should still get a usable popup.
        var header = Fallback(config.ConcurrentTranscodeLimitHeader, DefaultDeniedHeader);
        var text = TranscodeGuardRules.FormatConcurrentTranscodeLimitMessage(
            Fallback(config.ConcurrentTranscodeLimitMessage, DefaultDeniedMessage),
            decision.ActiveTranscodes,
            decision.Limit);

        // Delivery is best effort. A client that cannot show a popup is still refused.
        await _clientMessageService.SendMessageAsync(
            session,
            new MessageCommand
            {
                Header = header,
                Text = text,
                TimeoutMs = config.MessageTimeoutMs
            },
            BuildBrowserNagArguments(session, config, request, header, text),
            config.UseStickyConcurrentTranscodeLimitMessages,
            "simultaneous transcode limit block",
            $"{decision.ActiveTranscodes} other device(s) transcoding against a limit of {decision.Limit}",
            config.EnableLogging,
            _logger,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// For Jellyfin Web, adds the install warning to the refusal's own DisplayMessage, as the
    /// other refusals do: Jellyfin Web's playback error covers the toast.
    /// </summary>
    private Dictionary<string, string>? BuildBrowserNagArguments(
        SessionInfo session,
        PluginConfiguration config,
        TranscodeLimitRequest request,
        string header,
        string text)
    {
        if (!BrowserNagRules.ShouldUseBrowserNag(session, config))
        {
            return null;
        }

        var nagId = Guid.NewGuid();
        if (config.EnableLogging)
        {
            BestEffort(() => _logger.LogInformation(
                "Session {SessionId} is Jellyfin Web ({DeviceName}); attaching browser install warning {NagId} to the simultaneous transcode refusal for user {UserName}",
                session.Id,
                session.DeviceName ?? "Unknown",
                nagId.ToString("N"),
                session.UserName ?? "Unknown"));
        }

        return BrowserNagRules.BuildRefusalArguments(config, request.TranscodeReasons, nagId, header, text);
    }

    private static string Fallback(string? configured, string defaultText)
        => string.IsNullOrWhiteSpace(configured) ? defaultText : configured;

    /// <summary>
    /// Identifies "this client, this item" across renegotiation, matching the other guards so one
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
}
