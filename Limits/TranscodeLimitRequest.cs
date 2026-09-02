using System;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.TranscodeGuard.Limits;

/// <summary>
/// Everything <see cref="TranscodeLimitGuard"/> needs about a transcode Jellyfin is about to
/// launch, lifted out of <c>StreamState</c> so the policy can be exercised without a live server.
/// </summary>
public sealed class TranscodeLimitRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether the streaming request asked for video.
    /// False for audio-only streams, which the login nag never counts.
    /// </summary>
    public bool IsVideoRequest { get; set; }

    /// <summary>
    /// Gets or sets Jellyfin's reasons for this transcode. Zero when the client sent none, which
    /// reads as a bitrate-only transcode and is never refused.
    /// </summary>
    public TranscodeReason TranscodeReasons { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is a live stream, which is the request-side
    /// stand-in for the Live TV test the stored count uses.
    /// </summary>
    /// <remarks>
    /// A streaming request carries a media source, not the <c>BaseItemDto</c> the counter checks
    /// with <c>IsLiveTvItem</c>, so the two cannot be the same test. This one is deliberately the
    /// wider of the two: over-including a live stream only means not refusing something, while
    /// under-including it would refuse a channel against a count it never contributed to.
    /// </remarks>
    public bool IsLiveStream { get; set; }

    /// <summary>
    /// Gets or sets the requesting device ID. The correlation key for the client message.
    /// </summary>
    public string? DeviceId { get; set; }

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
