using System;

namespace Jellyfin.Plugin.TranscodeGuard.Gpu;

/// <summary>
/// Everything the guard needs about a transcode Jellyfin is about to launch, lifted out of
/// <c>StreamState</c> so the policy can be exercised without a live server.
/// </summary>
public sealed class GpuTranscodeRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether the streaming request asked for video.
    /// False for audio-only streams.
    /// </summary>
    public bool IsVideoRequest { get; set; }

    /// <summary>
    /// Gets or sets Jellyfin's chosen output video codec. "copy" means remux/Direct Stream.
    /// </summary>
    public string? OutputVideoCodec { get; set; }

    /// <summary>
    /// Gets or sets the FFmpeg arguments Jellyfin built for this job.
    /// </summary>
    public string? CommandLineArguments { get; set; }

    /// <summary>
    /// Gets or sets the source video width in pixels.
    /// </summary>
    public int? SourceWidth { get; set; }

    /// <summary>
    /// Gets or sets the source video height in pixels.
    /// </summary>
    public int? SourceHeight { get; set; }

    /// <summary>
    /// Gets or sets the source video bit depth.
    /// </summary>
    public int? SourceBitDepth { get; set; }

    /// <summary>
    /// Gets or sets the source video codec.
    /// </summary>
    public string? SourceCodec { get; set; }

    /// <summary>
    /// Gets or sets the source codec reference-frame count.
    /// </summary>
    public int? SourceRefFrames { get; set; }

    /// <summary>
    /// Gets or sets the source frame rate.
    /// </summary>
    public float? SourceFramerate { get; set; }

    /// <summary>
    /// Gets or sets the source pixel format, for calibration logs and future profile refinement.
    /// </summary>
    public string? SourcePixelFormat { get; set; }

    /// <summary>
    /// Gets or sets the source dynamic-range classification, for calibration logs.
    /// </summary>
    public string? SourceVideoRangeType { get; set; }

    /// <summary>
    /// Gets or sets the output video width in pixels.
    /// </summary>
    public int? OutputWidth { get; set; }

    /// <summary>
    /// Gets or sets the output video height in pixels.
    /// </summary>
    public int? OutputHeight { get; set; }

    /// <summary>
    /// Gets or sets the output video bit depth.
    /// </summary>
    public int? OutputBitDepth { get; set; }

    /// <summary>
    /// Gets or sets the output frame rate.
    /// </summary>
    public float? OutputFramerate { get; set; }

    /// <summary>
    /// Gets or sets the output codec reference-frame count.
    /// </summary>
    public int? OutputRefFrames { get; set; }

    /// <summary>
    /// Gets or sets a stable path identifying duplicate starts for the same FFmpeg job.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets the requesting device ID. The correlation key for the client message.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Gets or sets the play session ID. The init and first media-segment starts share it, while
    /// a client renegotiation receives a new one.
    /// </summary>
    public string? PlaySessionId { get; set; }

    /// <summary>
    /// Gets or sets the authenticated user, or <see cref="Guid.Empty"/> when unknown.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the item being played.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item name, for logging only.
    /// </summary>
    public string? ItemName { get; set; }
}
