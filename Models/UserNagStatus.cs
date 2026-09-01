using System;

namespace Jellyfin.Plugin.TranscodeGuard.Models;

public class UserNagStatus
{
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Number of bad transcodes in the configured time window.
    /// </summary>
    public int BadTranscodeCount { get; set; }

    /// <summary>
    /// True if the user has a recorded "good playback" credit after their most recent bad transcode.
    /// </summary>
    public bool HasImprovementCredit { get; set; }

    /// <summary>
    /// True if we've already sent the login/open nag within the configured time window.
    /// </summary>
    public bool NaggedRecently { get; set; }

    public DateTime? LastBadTranscodeUtc { get; set; }

    public DateTime? LastNagUtc { get; set; }
}
