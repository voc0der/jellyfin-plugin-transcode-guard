using System;
using System.Globalization;
using System.Threading;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.TranscodeGuard.Limits;

/// <summary>
/// The outcome of one simultaneous transcode check. An admitted video transcode carries the slot
/// that holds its device's place in the user's count.
/// </summary>
public readonly struct ConcurrentTranscodeDecision
{
    private ConcurrentTranscodeDecision(bool isAdmitted, int activeTranscodes, int limit, ConcurrentTranscodeSlot? slot)
    {
        IsAdmitted = isAdmitted;
        ActiveTranscodes = activeTranscodes;
        Limit = limit;
        Slot = slot;
    }

    /// <summary>
    /// Gets a value indicating whether Jellyfin may launch this transcode.
    /// </summary>
    public bool IsAdmitted { get; }

    /// <summary>
    /// Gets how many of the user's other devices were already transcoding.
    /// </summary>
    public int ActiveTranscodes { get; }

    /// <summary>
    /// Gets the maximum that applied to the user.
    /// </summary>
    public int Limit { get; }

    /// <summary>
    /// Gets the slot an admitted video transcode holds, or null when nothing is being counted.
    /// </summary>
    internal ConcurrentTranscodeSlot? Slot { get; }

    /// <summary>
    /// Gets a decision that lets the transcode through without counting it. Every path that
    /// cannot reach an answer returns this, so the guard always fails open.
    /// </summary>
    public static ConcurrentTranscodeDecision Allowed { get; } = new(true, 0, 0, null);

    internal static ConcurrentTranscodeDecision Admitted(ConcurrentTranscodeSlot slot)
        => new(true, 0, 0, slot);

    /// <summary>
    /// Builds a refusal for a user whose other devices are already at or over their maximum.
    /// </summary>
    /// <param name="activeTranscodes">The user's other devices that are transcoding.</param>
    /// <param name="limit">The maximum that applied to the user.</param>
    /// <returns>The refusal.</returns>
    public static ConcurrentTranscodeDecision Denied(int activeTranscodes, int limit)
        => new(false, activeTranscodes, limit, null);

    /// <summary>
    /// Builds the server-side refusal text. Like the other refusals, this travels in the exception
    /// and reaches the log, not the client.
    /// </summary>
    /// <returns>The server-side refusal reason.</returns>
    public string BuildRefusalReason()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "Transcode Guard refused this transcode: the user already has {0} video transcode(s) running on other devices, at or over their limit of {1} at a time.",
            ActiveTranscodes,
            Limit);
    }
}

/// <summary>
/// Holds one device's place in its user's simultaneous transcode count, from admission until the
/// FFmpeg job it launched exits.
/// </summary>
/// <remarks>
/// Taken before FFmpeg launches, so two devices pressing play at the same moment cannot both be
/// admitted into the last free place.
/// </remarks>
internal sealed class ConcurrentTranscodeSlot : IDisposable
{
    private ConcurrentTranscodeGuard? _owner;

    internal ConcurrentTranscodeSlot(ConcurrentTranscodeGuard owner, Guid userId, string deviceKey)
    {
        _owner = owner;
        UserId = userId;
        DeviceKey = deviceKey;
    }

    internal Guid UserId { get; }

    internal string DeviceKey { get; }

    /// <summary>
    /// Gets the launched job, or null while the transcode has been admitted but not launched.
    /// Only written under the owning guard's lock.
    /// </summary>
    internal TranscodingJob? Job { get; set; }

    /// <summary>
    /// Binds the launched job. From here the slot lasts exactly as long as that job runs.
    /// </summary>
    /// <param name="job">The job Jellyfin launched.</param>
    internal void MarkLaunched(TranscodingJob job)
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.CompleteSlot(this, job);
    }

    /// <summary>
    /// Gives the place back when the transcode never launched. Does nothing once launched.
    /// </summary>
    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.CompleteSlot(this, null);
    }
}
