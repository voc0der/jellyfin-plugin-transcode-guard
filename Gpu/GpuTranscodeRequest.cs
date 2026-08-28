using System;

namespace Jellyfin.Plugin.TranscodeNag.Gpu;

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
    /// Gets or sets the requesting device ID. The correlation key for the client message.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Gets or sets the play session ID, used to keep repeated attempts at one playback together.
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
