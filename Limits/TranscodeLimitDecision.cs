using System.Globalization;

namespace Jellyfin.Plugin.TranscodeGuard.Limits;

/// <summary>
/// The outcome of one transcode limit check, carrying the numbers behind a refusal so the caller
/// does not have to read the event store a second time to explain it.
/// </summary>
public readonly struct TranscodeLimitDecision
{
    private TranscodeLimitDecision(bool isAdmitted, int transcodeCount, int threshold, string timeWindowLabel)
    {
        IsAdmitted = isAdmitted;
        TranscodeCount = transcodeCount;
        Threshold = threshold;
        TimeWindowLabel = timeWindowLabel;
    }

    /// <summary>
    /// Gets a value indicating whether Jellyfin may launch this transcode.
    /// </summary>
    public bool IsAdmitted { get; }

    /// <summary>
    /// Gets the user's counted transcodes in the configured window.
    /// </summary>
    public int TranscodeCount { get; }

    /// <summary>
    /// Gets the configured limit the count was measured against.
    /// </summary>
    public int Threshold { get; }

    /// <summary>
    /// Gets the human-readable window the count covers, for example "week".
    /// </summary>
    public string TimeWindowLabel { get; }

    /// <summary>
    /// Gets a decision that lets the transcode through. Every path that is not a refusal returns
    /// this, so a guard that cannot reach an answer always fails open.
    /// </summary>
    public static TranscodeLimitDecision Allowed { get; } = new(true, 0, 0, "week");

    /// <summary>
    /// Builds a refusal for a user who is at or over the limit.
    /// </summary>
    /// <param name="transcodeCount">The user's counted transcodes.</param>
    /// <param name="threshold">The configured limit.</param>
    /// <param name="timeWindowLabel">The window the count covers.</param>
    /// <returns>The refusal.</returns>
    public static TranscodeLimitDecision Denied(int transcodeCount, int threshold, string timeWindowLabel)
        => new(false, transcodeCount, threshold, timeWindowLabel);

    /// <summary>
    /// Builds the server-side refusal text. This travels in the exception, not to the client:
    /// Jellyfin only returns exception messages to callers in a Development environment.
    /// </summary>
    /// <returns>The server-side refusal reason.</returns>
    public string BuildRefusalReason()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "Transcode Guard refused this transcode: the user has {0} counted transcodes in the last {1}, at or over the configured limit of {2}.",
            TranscodeCount,
            TimeWindowLabel,
            Threshold);
    }
}
