namespace Jellyfin.Plugin.TranscodeGuard.Models;

/// <summary>
/// Stored alongside events.json to support rate limiting and "improvement" credits.
/// </summary>
public enum NagEventKind
{
    /// <summary>
    /// A "bad" transcode caused by format/codec incompatibility (what we nag about).
    /// </summary>
    BadTranscode = 0,

    /// <summary>
    /// A good playback (direct play / direct stream) recorded as a "credit" after a bad transcode.
    /// This is used to temporarily suppress login/open nags until the next bad transcode.
    /// </summary>
    ImprovementCredit = 1,

    /// <summary>
    /// A login/open nag was sent to the user. Used to enforce "only once per week/month".
    /// </summary>
    NagSent = 2
}
