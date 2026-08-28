using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TranscodeNag.Configuration;
using Jellyfin.Plugin.TranscodeNag.Messaging;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeNag.Gpu;

/// <summary>
/// Admission control for GPU-backed video transcodes.
/// </summary>
/// <remarks>
/// This is admission control, not a GPU scheduler. A reading is taken immediately before Jellyfin
/// is allowed to launch FFmpeg, and there is inherently a race between that reading and the
/// allocation FFmpeg goes on to make. Clearing the threshold does not guarantee the allocation
/// succeeds; the point is to refuse the predictable failures when VRAM is plainly exhausted.
/// </remarks>
public sealed class GpuResourceGuard
{
    // One press of play can produce several refusals. On a playback error jellyfin-web's
    // playbackmanager falls back to progressively stricter transcode options, and each fallback
    // is a fresh /Items/{id}/PlaybackInfo call, so Jellyfin mints a new PlaySessionId every time.
    // Those retries are indistinguishable from a new request except by timing.
    //
    // Timing separates them well, because the fallback delay is setTimeout(..., 100) and the
    // chain is bounded - enablePlaybackRetryWithTranscoding stops once video and audio stream
    // copy are both disallowed. A whole burst is therefore around three hops of ~100ms plus two
    // round trips: comfortably under a second. A person dismissing the error dialog and pressing
    // play again takes far longer.
    //
    // So this is a debounce on the gap between refusals, not a window from the first one: a burst
    // collapses to one popup however long it runs, and any lull announces the next refusal. Three
    // seconds leaves several times the headroom a real burst needs while still announcing a
    // deliberate re-open.
    private static readonly TimeSpan DefaultNotificationQuietPeriod = TimeSpan.FromSeconds(3);

    private const string DefaultDeniedHeader = "Transcoding unavailable";
    private const string DefaultDeniedMessage = "GPU resources are currently busy. Please try again later or use Direct Play.";

    private readonly IGpuMemoryProvider _gpuMemoryProvider;
    private readonly IClientMessageService _clientMessageService;
    private readonly ILogger<GpuResourceGuard> _logger;
    private readonly Func<PluginConfiguration?> _configurationAccessor;

    private readonly Dictionary<string, DateTimeOffset> _lastRefusalUtc = new(StringComparer.Ordinal);
    private readonly TimeSpan _notificationQuietPeriod;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _suppressionLock = new();

    public GpuResourceGuard(
        IGpuMemoryProvider gpuMemoryProvider,
        IClientMessageService clientMessageService,
        ILogger<GpuResourceGuard> logger)
        : this(gpuMemoryProvider, clientMessageService, logger, () => Plugin.Instance?.Configuration)
    {
    }

    internal GpuResourceGuard(
        IGpuMemoryProvider gpuMemoryProvider,
        IClientMessageService clientMessageService,
        ILogger<GpuResourceGuard> logger,
        Func<PluginConfiguration?> configurationAccessor)
        : this(
            gpuMemoryProvider,
            clientMessageService,
            logger,
            configurationAccessor,
            DefaultNotificationQuietPeriod,
            () => DateTimeOffset.UtcNow)
    {
    }

    internal GpuResourceGuard(
        IGpuMemoryProvider gpuMemoryProvider,
        IClientMessageService clientMessageService,
        ILogger<GpuResourceGuard> logger,
        Func<PluginConfiguration?> configurationAccessor,
        TimeSpan notificationQuietPeriod,
        Func<DateTimeOffset> clock)
    {
        _gpuMemoryProvider = gpuMemoryProvider;
        _clientMessageService = clientMessageService;
        _logger = logger;
        _configurationAccessor = configurationAccessor;
        _notificationQuietPeriod = notificationQuietPeriod;
        _clock = clock;
    }

    /// <summary>
    /// Decides whether Jellyfin may launch this transcode, notifying the requesting client on refusal.
    /// </summary>
    /// <param name="request">The transcode Jellyfin is about to launch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the transcode may proceed.</returns>
    public async Task<bool> IsAdmittedAsync(GpuTranscodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var config = _configurationAccessor();
        if (config == null)
        {
            // The plugin is not fully loaded; never stand between Jellyfin and playback.
            return true;
        }

        var requiresGpu = GpuAdmissionPolicy.RequiresGpuVideoTranscode(
            request.IsVideoRequest,
            request.OutputVideoCodec,
            request.CommandLineArguments);

        GpuMemoryQueryResult? memory = null;
        if (GpuAdmissionPolicy.RequiresGpuQuery(config, requiresGpu))
        {
            memory = await _gpuMemoryProvider.GetFreeMemoryAsync(
                config.GpuIndex,
                config.GpuCheckTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
        }

        var outcome = GpuAdmissionPolicy.Evaluate(config, requiresGpu, memory);

        if (outcome == GpuAdmissionOutcome.AllowedQueryFailed)
        {
            LogQueryFailure(config, memory);
        }

        if (outcome != GpuAdmissionOutcome.Denied)
        {
            _logger.LogDebug(
                "GPU resource guard allowed transcode of {ItemName}: {Outcome}",
                request.ItemName ?? "Unknown",
                outcome);
            return true;
        }

        // The decision is made. Everything past this point is notification and logging, and none
        // of it may reverse the refusal: an exception escaping here would reach the decorator's
        // fail-open catch and admit the very transcode we just judged unsafe. ClientMessageService
        // does not catch every failure Jellyfin's WebSocket send path can raise.
        try
        {
            await DenyAsync(request, config, memory!.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(
                ex,
                "Failed to notify the client about the refused GPU transcode of {ItemName}; the refusal still stands",
                request.ItemName ?? "Unknown");
        }

        return false;
    }

    /// <summary>
    /// Builds the server-side refusal text. This travels in the exception, not to the client:
    /// Jellyfin only returns exception messages to callers in a Development environment.
    /// </summary>
    /// <returns>The server-side refusal reason.</returns>
    public string BuildRefusalReason()
    {
        var config = _configurationAccessor();
        if (config == null)
        {
            return "Transcode Nag refused this hardware transcode: insufficient free GPU memory.";
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "Transcode Nag refused this hardware transcode: free GPU memory on GPU {0} is below the configured minimum of {1} MiB.",
            config.GpuIndex,
            config.MinimumFreeGpuMemoryMiB);
    }

    private async Task DenyAsync(
        GpuTranscodeRequest request,
        PluginConfiguration config,
        GpuMemoryQueryResult memory,
        CancellationToken cancellationToken)
    {
        var session = _clientMessageService.ResolveSession(request.DeviceId, request.UserId, request.ItemId);
        var notify = ShouldNotify(BuildSuppressionKey(request));

        if (!notify)
        {
            // Still refused - only the popup and the warning are de-duplicated.
            _logger.LogDebug(
                "GPU transcode blocked again for item {ItemName} within the notification suppression window",
                request.ItemName ?? "Unknown");
            return;
        }

        _logger.LogWarning(
            "GPU transcode blocked for session {SessionId}, user {UserName}, device {DeviceName}, item {ItemName}: free VRAM {FreeMiB} MiB below configured threshold {ThresholdMiB} MiB on GPU {GpuIndex}",
            session?.Id ?? "Unknown",
            session?.UserName ?? "Unknown",
            session?.DeviceName ?? "Unknown",
            request.ItemName ?? "Unknown",
            memory.FreeMiB,
            config.MinimumFreeGpuMemoryMiB,
            config.GpuIndex);

        if (session == null)
        {
            _logger.LogDebug(
                "No session could be correlated to the refused transcode of {ItemName}; the refusal still stands",
                request.ItemName ?? "Unknown");
            return;
        }

        // Delivery is best effort. A client that cannot show a popup is still refused.
        await _clientMessageService.SendMessageAsync(
            session,
            new MessageCommand
            {
                // An admin who blanks these fields should still get a usable popup.
                Header = Fallback(config.GpuGuardDeniedHeader, DefaultDeniedHeader),
                Text = Fallback(config.GpuGuardDeniedMessage, DefaultDeniedMessage),
                TimeoutMs = config.MessageTimeoutMs
            },
            "gpu guard denial",
            "Hardware transcode refused",
            config.EnableLogging,
            _logger,
            cancellationToken).ConfigureAwait(false);
    }

    private void LogQueryFailure(PluginConfiguration config, GpuMemoryQueryResult? memory)
    {
        if (!ShouldNotify("query-failure|" + config.GpuIndex.ToString(CultureInfo.InvariantCulture)))
        {
            return;
        }

        _logger.LogWarning(
            "Unable to query free VRAM for GPU {GpuIndex}; allowing playback because GPU resource guard is fail-open ({Reason})",
            config.GpuIndex,
            memory?.FailureReason ?? "no result");
    }

    private static string Fallback(string? configured, string defaultText)
        => string.IsNullOrWhiteSpace(configured) ? defaultText : configured;

    /// <summary>
    /// Identifies "this client, this item" across renegotiation. Deliberately excludes
    /// PlaySessionId: Jellyfin issues a new one per PlaybackInfo call, so including it would make
    /// every renegotiated retry look like a first refusal.
    /// </summary>
    /// <param name="request">The refused transcode.</param>
    /// <returns>The suppression key.</returns>
    private static string BuildSuppressionKey(GpuTranscodeRequest request)
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
            PruneExpired(now);
            _lastRefusalUtc[key] = now;

            return notify;
        }
    }

    private void PruneExpired(DateTimeOffset now)
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
}
