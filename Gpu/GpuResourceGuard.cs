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
/// This is admission control, not a GPU scheduler. A fresh reading is taken immediately before
/// Jellyfin launches FFmpeg and compared with a measured budget for that job. Short-lived in-flight
/// reservations cover the gap before a new process's allocation appears in nvidia-smi. Admission
/// cannot guarantee every driver allocation succeeds; it rejects the predictable failures while
/// allowing smaller jobs to use the VRAM that is genuinely left.
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
    private static readonly TimeSpan DefaultInFlightReservationLifetime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProcessObservationInterval = TimeSpan.FromMilliseconds(250);
    private const int ProcessObservationSampleCount = 3;

    private const string DefaultDeniedHeader = "Transcoding unavailable";
    private const string DefaultDeniedMessage = "GPU resources are currently busy. Please try again later or use Direct Play.";

    private readonly IGpuMemoryProvider _gpuMemoryProvider;
    private readonly IClientMessageService _clientMessageService;
    private readonly ILogger<GpuResourceGuard> _logger;
    private readonly Func<PluginConfiguration?> _configurationAccessor;

    private readonly Dictionary<string, DateTimeOffset> _lastRefusalUtc = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InFlightReservation> _inFlightReservations = new(StringComparer.Ordinal);
    private readonly TimeSpan _notificationQuietPeriod;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _suppressionLock = new();
    private readonly object _reservationLock = new();
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private long _nextReservationId;

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
        var attempt = await AssessAsync(request, reserveInFlight: false, cancellationToken).ConfigureAwait(false);
        return attempt.IsAdmitted;
    }

    /// <summary>
    /// Decides whether Jellyfin may launch and reserves the job's budget until FFmpeg allocates it.
    /// </summary>
    internal Task<GpuAdmissionAttempt> TryReserveAsync(
        GpuTranscodeRequest request,
        CancellationToken cancellationToken)
        => AssessAsync(request, reserveInFlight: true, cancellationToken);

    private async Task<GpuAdmissionAttempt> AssessAsync(
        GpuTranscodeRequest request,
        bool reserveInFlight,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var config = _configurationAccessor();
        if (config == null)
        {
            // The plugin is not fully loaded; never stand between Jellyfin and playback.
            return GpuAdmissionAttempt.AllowedWithoutReservation;
        }

        var requiresGpu = GpuAdmissionPolicy.RequiresGpuVideoTranscode(
            request.IsVideoRequest,
            request.OutputVideoCodec,
            request.CommandLineArguments);

        var features = requiresGpu
            ? NvidiaTranscodeDetector.Analyze(request.CommandLineArguments)
            : default;
        var gpuIndex = features.GpuIndex ?? config.GpuIndex;
        int? reservationGpuIndex = features.HasConflictingGpuIndices ? null : gpuIndex;
        var estimate = requiresGpu ? GpuVramEstimator.Estimate(request) : GpuVramEstimate.Zero;
        var reservationKey = BuildReservationKey(reservationGpuIndex, request);
        var inFlightBudgetMiB = 0;
        var decisionJobBudgetMiB = estimate.BudgetMiB;
        var currentJobAlreadyTracked = false;
        GpuMemoryQueryResult? memory = null;
        GpuAdmissionOutcome outcome;
        GpuAdmissionReservation? reservation = null;

        if (!GpuAdmissionPolicy.RequiresGpuQuery(config, requiresGpu))
        {
            outcome = GpuAdmissionPolicy.Evaluate(
                config,
                requiresGpu,
                memory,
                estimate.BudgetMiB);
        }
        else
        {
            // Query, decide, and record the temporary budget as one serial operation. Otherwise
            // simultaneous starts can all spend the same pre-allocation free-memory reading.
            await _admissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (features.HasConflictingGpuIndices)
                {
                    // A contradictory FFmpeg command does not identify which GPU will allocate.
                    // Treat that telemetry as unavailable instead of querying the wrong device.
                    memory = GpuMemoryQueryResult.Failed("FFmpeg selected conflicting GPU indices");
                }
                else
                {
                    try
                    {
                        memory = await _gpuMemoryProvider.GetFreeMemoryAsync(
                            gpuIndex,
                            config.GpuCheckTimeoutMilliseconds,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
                    {
                        // Providers are expected to return a failed result, but a provider bug must
                        // still preserve fail-open behaviour. Keep a reservation for the launch below
                        // so the next successful query cannot spend the same pre-allocation memory.
                        memory = GpuMemoryQueryResult.Failed(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "GPU memory provider threw {0}",
                                ex.GetType().Name));
                    }
                }

                inFlightBudgetMiB = GetInFlightBudgetMiB(
                    gpuIndex,
                    reservationKey,
                    out currentJobAlreadyTracked);
                if (currentJobAlreadyTracked)
                {
                    // Repeated StartFfMpeg calls for one server output path reuse one process.
                    // Neither this job nor unrelated pending jobs need to be charged again merely
                    // to let Jellyfin retrieve another segment from that existing job.
                    decisionJobBudgetMiB = 0;
                    inFlightBudgetMiB = 0;
                }

                outcome = GpuAdmissionPolicy.Evaluate(
                    config,
                    requiresGpu,
                    memory,
                    decisionJobBudgetMiB,
                    inFlightBudgetMiB);

                if (reserveInFlight
                    && outcome is GpuAdmissionOutcome.AllowedSufficientMemory
                        or GpuAdmissionOutcome.AllowedQueryFailed)
                {
                    reservation = AddReservation(reservationGpuIndex, reservationKey, estimate.BudgetMiB);
                }
            }
            finally
            {
                _admissionGate.Release();
            }
        }

        if (outcome == GpuAdmissionOutcome.AllowedQueryFailed)
        {
            BestEffort(() => LogQueryFailure(gpuIndex, memory));
        }

        if (outcome != GpuAdmissionOutcome.Denied)
        {
            if (outcome == GpuAdmissionOutcome.AllowedSufficientMemory)
            {
                BestEffort(() => _logger.LogDebug(
                    "GPU resource guard allowed transcode of {ItemName}: free VRAM {FreeMiB} MiB fits decision budget {DecisionBudgetMiB} MiB plus in-flight budget {InFlightBudgetMiB} MiB on GPU {GpuIndex}; profile budget {ProfileBudgetMiB} MiB, existing output job {ExistingOutputJob}",
                    request.ItemName ?? "Unknown",
                    memory!.Value.FreeMiB,
                    decisionJobBudgetMiB,
                    inFlightBudgetMiB,
                    gpuIndex,
                    estimate.BudgetMiB,
                    currentJobAlreadyTracked));
            }
            else
            {
                BestEffort(() => _logger.LogDebug(
                    "GPU resource guard allowed transcode of {ItemName}: {Outcome}",
                    request.ItemName ?? "Unknown",
                    outcome));
            }

            return new GpuAdmissionAttempt(true, reservation);
        }

        // The decision is made. Everything past this point is notification and logging, and none
        // of it may reverse the refusal: an exception escaping here would reach the decorator's
        // fail-open catch and admit the very transcode we just judged unsafe. ClientMessageService
        // does not catch every failure Jellyfin's WebSocket send path can raise.
        try
        {
            await DenyAsync(
                request,
                config,
                memory!.Value,
                estimate,
                inFlightBudgetMiB,
                gpuIndex,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BestEffort(() => _logger.LogError(
                ex,
                "Failed to notify the client about the refused GPU transcode of {ItemName}; the refusal still stands",
                request.ItemName ?? "Unknown"));
        }

        return GpuAdmissionAttempt.Denied;
    }

    /// <summary>
    /// Builds the server-side refusal text. This travels in the exception, not to the client:
    /// Jellyfin only returns exception messages to callers in a Development environment.
    /// </summary>
    /// <returns>The server-side refusal reason.</returns>
    public string BuildRefusalReason(GpuTranscodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var config = _configurationAccessor();
        if (config == null)
        {
            return "Transcode Nag refused this hardware transcode: insufficient free GPU memory.";
        }

        var estimate = GpuVramEstimator.Estimate(request);
        var selectedGpuIndex = NvidiaTranscodeDetector.Analyze(request.CommandLineArguments).GpuIndex;

        return string.Format(
            CultureInfo.InvariantCulture,
            "Transcode Nag refused this hardware transcode: its conservative {1} MiB VRAM budget does not fit in the memory currently free on GPU {0}.",
            selectedGpuIndex ?? config.GpuIndex,
            estimate.BudgetMiB);
    }

    private async Task DenyAsync(
        GpuTranscodeRequest request,
        PluginConfiguration config,
        GpuMemoryQueryResult memory,
        GpuVramEstimate estimate,
        int inFlightBudgetMiB,
        int gpuIndex,
        CancellationToken cancellationToken)
    {
        var session = _clientMessageService.ResolveSession(request.DeviceId, request.UserId, request.ItemId);
        var notify = ShouldNotify(BuildSuppressionKey(request));

        if (!notify)
        {
            // Still refused - only the popup and the warning are de-duplicated.
            BestEffort(() => _logger.LogDebug(
                "GPU transcode blocked again for item {ItemName} within the notification suppression window",
                request.ItemName ?? "Unknown"));
            return;
        }

        BestEffort(() => _logger.LogWarning(
            "GPU transcode blocked for session {SessionId}, user {UserName}, device {DeviceName}, item {ItemName}: free VRAM {FreeMiB} MiB cannot fit conservative job budget {JobBudgetMiB} MiB plus in-flight budget {InFlightBudgetMiB} MiB on GPU {GpuIndex}; shape {SourceWidth}x{SourceHeight} {SourceBitDepth}-bit {SourceCodec} to {OutputWidth}x{OutputHeight} {OutputBitDepth}-bit {OutputCodec}, CUDA tonemap {UsesTonemap}, metadata fallback {UsedFallbackMetadata}",
            session?.Id ?? "Unknown",
            session?.UserName ?? "Unknown",
            session?.DeviceName ?? "Unknown",
            request.ItemName ?? "Unknown",
            memory.FreeMiB,
            estimate.BudgetMiB,
            inFlightBudgetMiB,
            gpuIndex,
            estimate.SourceWidth,
            estimate.SourceHeight,
            estimate.SourceBitDepth,
            request.SourceCodec ?? "unknown",
            estimate.OutputWidth,
            estimate.OutputHeight,
            estimate.OutputBitDepth,
            request.OutputVideoCodec ?? "unknown",
            estimate.UsesTonemap,
            estimate.UsedFallbackMetadata));

        if (session == null)
        {
            BestEffort(() => _logger.LogDebug(
                "No session could be correlated to the refused transcode of {ItemName}; the refusal still stands",
                request.ItemName ?? "Unknown"));
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
            config.UseStickyGpuGuardMessages,
            "gpu guard denial",
            "Hardware transcode refused",
            config.EnableLogging,
            _logger,
            cancellationToken).ConfigureAwait(false);
    }

    private void LogQueryFailure(int gpuIndex, GpuMemoryQueryResult? memory)
    {
        if (!ShouldNotify("query-failure|" + gpuIndex.ToString(CultureInfo.InvariantCulture)))
        {
            return;
        }

        _logger.LogWarning(
            "Unable to query free VRAM for GPU {GpuIndex}; allowing playback because GPU resource guard is fail-open ({Reason})",
            gpuIndex,
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

    private static string BuildReservationKey(int? gpuIndex, GpuTranscodeRequest request)
    {
        // Jellyfin's output path identifies the FFmpeg job. PlaySessionId is client supplied and
        // can be reused across distinct jobs, so using it here could merge two real allocations
        // and admit more memory than is available.
        if (!string.IsNullOrWhiteSpace(request.OutputPath))
        {
            return string.Join(
                '|',
                gpuIndex?.ToString(CultureInfo.InvariantCulture) ?? "any",
                "path",
                request.OutputPath);
        }

        // The decorator always supplies an output path. If another caller does not, never merge
        // identities on weaker client metadata: temporary over-reservation is safer than treating
        // two different FFmpeg processes as one.
        return string.Join(
            '|',
            gpuIndex?.ToString(CultureInfo.InvariantCulture) ?? "any",
            "unknown",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    }

    private static void BestEffort(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Logging and notification bookkeeping are diagnostic only. In particular, a logger
            // implementation must not reverse a denial or strand an admitted reservation.
        }
    }

    private int GetInFlightBudgetMiB(
        int gpuIndex,
        string currentReservationKey,
        out bool currentJobAlreadyTracked)
    {
        lock (_reservationLock)
        {
            PruneExpiredReservations(_clock());
            currentJobAlreadyTracked = _inFlightReservations.ContainsKey(currentReservationKey);

            var total = _inFlightReservations
                .Where(entry => !entry.Value.AllocationVisible
                    && (!entry.Value.GpuIndex.HasValue || entry.Value.GpuIndex.Value == gpuIndex)
                    && !string.Equals(entry.Key, currentReservationKey, StringComparison.Ordinal))
                .Sum(entry => (long)entry.Value.BudgetMiB);

            return (int)Math.Min(int.MaxValue, total);
        }
    }

    private GpuAdmissionReservation AddReservation(int? gpuIndex, string key, int budgetMiB)
    {
        long reservationId;
        lock (_reservationLock)
        {
            if (!_inFlightReservations.TryGetValue(key, out var reservation))
            {
                reservation = new InFlightReservation
                {
                    GpuIndex = gpuIndex,
                    Id = Interlocked.Increment(ref _nextReservationId)
                };
                _inFlightReservations.Add(key, reservation);
            }

            reservation.BudgetMiB = Math.Max(reservation.BudgetMiB, budgetMiB);
            reservation.ActiveHolders++;
            reservationId = reservation.Id;
        }

        return new GpuAdmissionReservation(this, key, reservationId);
    }

    internal void CompleteReservation(string key, long reservationId, bool launched)
    {
        lock (_reservationLock)
        {
            if (!_inFlightReservations.TryGetValue(key, out var reservation)
                || reservation.Id != reservationId)
            {
                return;
            }

            reservation.ActiveHolders = Math.Max(0, reservation.ActiveHolders - 1);
            if (launched)
            {
                reservation.WasLaunched = true;
                reservation.ExpiresUtc = _clock() + DefaultInFlightReservationLifetime;
            }

            if (reservation.ActiveHolders == 0 && !reservation.WasLaunched)
            {
                _inFlightReservations.Remove(key);
            }
        }
    }

    internal Task BeginProcessObservation(
        string key,
        long reservationId,
        int processId,
        GpuTranscodeRequest request)
    {
        if (_gpuMemoryProvider is not IGpuProcessMemoryProvider processMemoryProvider)
        {
            return Task.CompletedTask;
        }

        PluginConfiguration? config;
        try
        {
            config = _configurationAccessor();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Task.CompletedTask;
        }

        if (config == null)
        {
            return Task.CompletedTask;
        }

        lock (_reservationLock)
        {
            if (!_inFlightReservations.TryGetValue(key, out var reservation)
                || reservation.Id != reservationId
                || reservation.ObservationStarted)
            {
                return Task.CompletedTask;
            }

            reservation.ObservationStarted = true;
        }

        return ObserveProcessMemoryAsync(
            key,
            reservationId,
            processId,
            request,
            config.GpuCheckTimeoutMilliseconds,
            processMemoryProvider);
    }

    private async Task ObserveProcessMemoryAsync(
        string reservationKey,
        long reservationId,
        int processId,
        GpuTranscodeRequest request,
        int timeoutMilliseconds,
        IGpuProcessMemoryProvider processMemoryProvider)
    {
        try
        {
            var maxUsedMiB = -1;
            string? lastFailure = null;
            for (var sample = 0; sample < ProcessObservationSampleCount; sample++)
            {
                if (sample > 0)
                {
                    await Task.Delay(ProcessObservationInterval).ConfigureAwait(false);
                }

                var result = await processMemoryProvider.GetUsedMemoryAsync(
                    processId,
                    timeoutMilliseconds,
                    CancellationToken.None).ConfigureAwait(false);

                if (!result.Success)
                {
                    lastFailure = result.FailureReason;
                    continue;
                }

                maxUsedMiB = Math.Max(maxUsedMiB, result.UsedMiB);
                if (result.UsedMiB > 0)
                {
                    MarkAllocationVisible(reservationKey, reservationId);
                }
            }

            if (maxUsedMiB >= 0)
            {
                var estimate = GpuVramEstimator.Estimate(request);
                BestEffort(() => _logger.LogInformation(
                    "GPU VRAM calibration for FFmpeg PID {ProcessId}: observed {ActualMiB} MiB maximum across {SampleCount} samples versus {BudgetMiB} MiB budget; shape {SourceWidth}x{SourceHeight} {SourceBitDepth}-bit {SourcePixelFormat} {SourceCodec} at {SourceFramerate} fps to {OutputWidth}x{OutputHeight} {OutputBitDepth}-bit {OutputCodec}, CUDA tonemap {UsesTonemap}",
                    processId,
                    maxUsedMiB,
                    ProcessObservationSampleCount,
                    estimate.BudgetMiB,
                    estimate.SourceWidth,
                    estimate.SourceHeight,
                    estimate.SourceBitDepth,
                    request.SourcePixelFormat ?? "unknown",
                    request.SourceCodec ?? "unknown",
                    request.SourceFramerate,
                    estimate.OutputWidth,
                    estimate.OutputHeight,
                    estimate.OutputBitDepth,
                    request.OutputVideoCodec ?? "unknown",
                    estimate.UsesTonemap));
            }
            else
            {
                BestEffort(() => _logger.LogDebug(
                    "Could not attribute GPU memory to FFmpeg PID {ProcessId}; retaining the timed reservation ({Reason})",
                    processId,
                    lastFailure ?? "no process-memory result"));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Calibration is observational. It must never affect a process that already launched.
            BestEffort(() => _logger.LogDebug(ex, "GPU process-memory observation failed for PID {ProcessId}", processId));
        }
    }

    private void MarkAllocationVisible(string key, long reservationId)
    {
        lock (_reservationLock)
        {
            if (!_inFlightReservations.TryGetValue(key, out var reservation)
                || reservation.Id != reservationId)
            {
                return;
            }

            reservation.AllocationVisible = true;
        }
    }

    private void PruneExpiredReservations(DateTimeOffset now)
    {
        var expired = _inFlightReservations
            .Where(entry => entry.Value.ActiveHolders == 0
                && entry.Value.WasLaunched
                && entry.Value.ExpiresUtc <= now)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var key in expired)
        {
            _inFlightReservations.Remove(key);
        }
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

    private sealed class InFlightReservation
    {
        internal long Id { get; init; }

        internal int? GpuIndex { get; init; }

        internal int BudgetMiB { get; set; }

        internal int ActiveHolders { get; set; }

        internal bool WasLaunched { get; set; }

        internal DateTimeOffset ExpiresUtc { get; set; }

        internal bool ObservationStarted { get; set; }

        internal bool AllocationVisible { get; set; }
    }
}
