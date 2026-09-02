using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TranscodeGuard.Limits;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Streaming;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeGuard.Gpu;

/// <summary>
/// Wraps Jellyfin's <see cref="ITranscodeManager"/> so the transcode limit and the GPU guard run
/// immediately before FFmpeg is launched.
/// </summary>
/// <remarks>
/// <para>
/// Every streaming FFmpeg process in Jellyfin 10.10-10.11 starts through
/// <see cref="ITranscodeManager.StartFfMpeg"/> - <c>DynamicHlsController</c> (playlist and segment)
/// and <c>FileStreamResponseHelpers</c> (progressive). Refusing here means the process is never
/// created, which is the difference between one clean refusal and the retry storm of doomed
/// FFmpeg launches Jellyfin produces when CUDA cannot allocate.
/// </para>
/// <para>
/// The refusal uses Jellyfin's own <see cref="SecurityException"/>, which its exception middleware
/// maps to HTTP 403 and logs without a stack trace. The BCL type with the same name does not match
/// Jellyfin's middleware and must not be used here.
/// </para>
/// </remarks>
public sealed class GuardedTranscodeManager : ITranscodeManager, IDisposable
{
    private readonly ITranscodeManager _inner;
    private readonly GpuResourceGuard _guard;
    private readonly TranscodeLimitGuard _limitGuard;
    private readonly ILogger<GuardedTranscodeManager> _logger;
    private bool _disposed;

    public GuardedTranscodeManager(
        ITranscodeManager inner,
        GpuResourceGuard guard,
        TranscodeLimitGuard limitGuard,
        ILogger<GuardedTranscodeManager> logger)
    {
        _inner = inner;
        _guard = guard;
        _limitGuard = limitGuard;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TranscodingJob> StartFfMpeg(
        StreamState state,
        string outputPath,
        string commandLineArguments,
        Guid userId,
        TranscodingJobType transcodingJobType,
        CancellationTokenSource cancellationTokenSource,
        string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(cancellationTokenSource);

        // The per-user limit is settled first: it is a policy decision that does not depend on the
        // GPU's state, and refusing here means no VRAM reservation is spent on a job that is not
        // allowed to run anyway.
        var limitDecision = TranscodeLimitDecision.Allowed;

        try
        {
            limitDecision = await _limitGuard.AssessAsync(
                BuildLimitRequest(state, userId),
                cancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            // A broken guard must never break playback.
            BestEffort(() => _logger.LogError(ex, "Transcode limit guard failed; allowing the transcode to proceed"));
        }

        if (!limitDecision.IsAdmitted)
        {
            // See the note below on SecurityException: this is Jellyfin's type, mapping to a clean
            // HTTP 403 rather than a 500 with a stack trace.
            throw new SecurityException(limitDecision.BuildRefusalReason());
        }

        var admitted = true;
        GpuTranscodeRequest? request = null;
        GpuAdmissionReservation? reservation = null;

        try
        {
            request = BuildRequest(state, outputPath, commandLineArguments, userId);
            var attempt = await _guard.TryReserveAsync(
                request,
                cancellationTokenSource.Token).ConfigureAwait(false);
            admitted = attempt.IsAdmitted;
            reservation = attempt.Reservation;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            // A broken guard must never break playback.
            BestEffort(() => _logger.LogError(ex, "GPU resource guard failed; allowing the transcode to proceed"));
        }

        if (!admitted)
        {
            // MediaBrowser.Controller.Net.SecurityException, specifically - NOT the BCL type of
            // the same name. Jellyfin's ExceptionMiddleware has no "using System.Security", so the
            // SecurityException in its switch is Jellyfin's own; throwing the BCL one falls through
            // to 500 with a full stack trace. This one maps to HTTP 403 and is logged as a single
            // line, which is what a policy refusal deserves - the guard has already logged the
            // detail, and a 40-line trace per attempt made a working guard read as a crash.
            reservation?.Dispose();

            string refusalReason;
            try
            {
                refusalReason = _guard.BuildRefusalReason(request!);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The denial is already final. Even configuration/log-message construction must
                // not change Jellyfin's clean 403 into a noisy 500 or let FFmpeg launch.
                BestEffort(() => _logger.LogError(ex, "Failed to build the GPU resource guard refusal reason"));
                refusalReason = "Transcode Guard refused this hardware transcode: insufficient free GPU memory.";
            }

            throw new SecurityException(refusalReason);
        }

        try
        {
            var job = await _inner.StartFfMpeg(
                state,
                outputPath,
                commandLineArguments,
                userId,
                transcodingJobType,
                cancellationTokenSource,
                workingDirectory).ConfigureAwait(false);
            int? processId = null;
            try
            {
                processId = job.Process?.Id;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                BestEffort(() => _logger.LogDebug(ex, "Could not read the launched FFmpeg process ID"));
            }

            if (reservation != null)
            {
                _ = reservation.MarkLaunched(processId, request);
            }
            return job;
        }
        catch
        {
            // StartFfMpeg can throw after it has created and started the process (for example while
            // waiting for its first output). We cannot prove that no allocation exists, so retain
            // the short race-window reservation rather than letting the next job spend it.
            if (reservation != null)
            {
                _ = reservation.MarkLaunched();
            }
            throw;
        }
    }

    private static TranscodeLimitRequest BuildLimitRequest(StreamState state, Guid userId)
    {
        var request = state.Request;

        return new TranscodeLimitRequest
        {
            IsVideoRequest = state.VideoRequest != null,
            // EncodingJobInfo reads TranscodeReasons off Request, so a partially constructed state
            // has none. No reasons reads as a bitrate-driven transcode, which is never refused.
            TranscodeReasons = request == null ? 0 : state.TranscodeReasons,
            // Both signals, because either alone is narrower than the Live TV the counter skips.
            IsLiveStream = state.MediaSource?.IsInfiniteStream == true
                || !string.IsNullOrEmpty(state.MediaSource?.LiveStreamId),
            DeviceId = request?.DeviceId,
            UserId = userId,
            ItemId = request?.Id ?? Guid.Empty,
            ItemName = state.MediaSource?.Name
        };
    }

    private static GpuTranscodeRequest BuildRequest(
        StreamState state,
        string outputPath,
        string commandLineArguments,
        Guid userId)
    {
        var request = state.Request;

        return new GpuTranscodeRequest
        {
            // Jellyfin has already made the playback decision; these are its answers, not ours.
            IsVideoRequest = state.VideoRequest != null,
            OutputVideoCodec = state.OutputVideoCodec,
            CommandLineArguments = commandLineArguments,
            SourceWidth = state.VideoStream?.Width,
            SourceHeight = state.VideoStream?.Height,
            SourceBitDepth = state.VideoStream?.BitDepth,
            SourceCodec = state.VideoStream?.Codec,
            SourceRefFrames = state.VideoStream?.RefFrames,
            SourceFramerate = state.VideoStream?.ReferenceFrameRate
                ?? state.VideoStream?.RealFrameRate
                ?? state.VideoStream?.AverageFrameRate,
            SourcePixelFormat = state.VideoStream?.PixelFormat,
            SourceVideoRangeType = state.VideoStream?.VideoRangeType.ToString(),
            // EncodingJobInfo derives these from Request; malformed or partially constructed
            // states can have no request. Preserve fail-open behaviour by treating them as unknown.
            OutputWidth = request == null ? null : state.OutputWidth,
            OutputHeight = request == null ? null : state.OutputHeight,
            OutputBitDepth = request == null ? null : state.TargetVideoBitDepth,
            // TargetFramerate is only the requested cap and is often null. In that case the
            // transcode retains the source rate, which still matters to surface pressure.
            OutputFramerate = request == null
                ? state.VideoStream?.ReferenceFrameRate
                    ?? state.VideoStream?.RealFrameRate
                    ?? state.VideoStream?.AverageFrameRate
                : state.TargetFramerate
                    ?? state.VideoStream?.ReferenceFrameRate
                    ?? state.VideoStream?.RealFrameRate
                    ?? state.VideoStream?.AverageFrameRate,
            OutputRefFrames = request == null ? null : state.TargetRefFrames,
            OutputPath = outputPath,
            DeviceId = request?.DeviceId,
            PlaySessionId = request?.PlaySessionId,
            UserId = userId,
            ItemId = request?.Id ?? Guid.Empty,
            ItemName = state.MediaSource?.Name
        };
    }

    private static void BestEffort(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A logger is diagnostic infrastructure. It must not change admission or fail-open
            // semantics if a custom provider throws from ILogger.Log.
        }
    }

    /// <inheritdoc />
    public TranscodingJob? GetTranscodingJob(string playSessionId)
        => _inner.GetTranscodingJob(playSessionId);

    /// <inheritdoc />
    public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type)
        => _inner.GetTranscodingJob(path, type);

    /// <inheritdoc />
    public void PingTranscodingJob(string playSessionId, bool? isUserPaused)
        => _inner.PingTranscodingJob(playSessionId, isUserPaused);

    /// <inheritdoc />
    public Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles)
        => _inner.KillTranscodingJobs(deviceId, playSessionId, deleteFiles);

    /// <inheritdoc />
    public void ReportTranscodingProgress(
        TranscodingJob job,
        StreamState state,
        TimeSpan? transcodingPosition,
        float? framerate,
        double? percentComplete,
        long? bytesTranscoded,
        int? bitRate)
        => _inner.ReportTranscodingProgress(job, state, transcodingPosition, framerate, percentComplete, bytesTranscoded, bitRate);

    /// <inheritdoc />
    public TranscodingJob? OnTranscodeBeginRequest(string path, TranscodingJobType type)
        => _inner.OnTranscodeBeginRequest(path, type);

    /// <inheritdoc />
    public void OnTranscodeEndRequest(TranscodingJob job)
        => _inner.OnTranscodeEndRequest(job);

    /// <inheritdoc />
    public ValueTask<IDisposable> LockAsync(string outputPath, CancellationToken cancellationToken)
        => _inner.LockAsync(outputPath, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The container created the inner manager through this decorator's factory, so its
        // lifetime is ours to end.
        (_inner as IDisposable)?.Dispose();
    }
}
