using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Streaming;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeNag.Gpu;

/// <summary>
/// Wraps Jellyfin's <see cref="ITranscodeManager"/> so the GPU guard runs immediately before
/// FFmpeg is launched.
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
/// The refusal is an <see cref="ArgumentException"/>, which is exactly how Jellyfin itself refuses
/// a video transcode a user lacks permission for, and which its exception middleware maps to
/// HTTP 400 - a terminal answer rather than a retryable server error.
/// </para>
/// </remarks>
public sealed class GuardedTranscodeManager : ITranscodeManager, IDisposable
{
    private readonly ITranscodeManager _inner;
    private readonly GpuResourceGuard _guard;
    private readonly ILogger<GuardedTranscodeManager> _logger;
    private bool _disposed;

    public GuardedTranscodeManager(
        ITranscodeManager inner,
        GpuResourceGuard guard,
        ILogger<GuardedTranscodeManager> logger)
    {
        _inner = inner;
        _guard = guard;
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

        var admitted = true;

        try
        {
            admitted = await _guard.IsAdmittedAsync(
                BuildRequest(state, commandLineArguments, userId),
                cancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            // A broken guard must never break playback.
            _logger.LogError(ex, "GPU resource guard failed; allowing the transcode to proceed");
        }

        if (!admitted)
        {
            // MediaBrowser.Controller.Net.SecurityException, specifically - NOT the BCL type of
            // the same name. Jellyfin's ExceptionMiddleware has no "using System.Security", so the
            // SecurityException in its switch is Jellyfin's own; throwing the BCL one falls through
            // to 500 with a full stack trace. This one maps to HTTP 403 and is logged as a single
            // line, which is what a policy refusal deserves - the guard has already logged the
            // detail, and a 40-line trace per attempt made a working guard read as a crash.
            throw new SecurityException(_guard.BuildRefusalReason());
        }

        return await _inner.StartFfMpeg(
            state,
            outputPath,
            commandLineArguments,
            userId,
            transcodingJobType,
            cancellationTokenSource,
            workingDirectory).ConfigureAwait(false);
    }

    private static GpuTranscodeRequest BuildRequest(StreamState state, string commandLineArguments, Guid userId)
    {
        var request = state.Request;

        return new GpuTranscodeRequest
        {
            // Jellyfin has already made the playback decision; these are its answers, not ours.
            IsVideoRequest = state.VideoRequest != null,
            OutputVideoCodec = state.OutputVideoCodec,
            CommandLineArguments = commandLineArguments,
            DeviceId = request?.DeviceId,
            PlaySessionId = request?.PlaySessionId,
            UserId = userId,
            ItemId = request?.Id ?? Guid.Empty,
            ItemName = state.MediaSource?.Name
        };
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
