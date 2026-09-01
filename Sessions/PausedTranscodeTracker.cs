using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.TranscodeGuard.Sessions;

/// <summary>
/// What should happen to one paused transcoding session on this tick.
/// </summary>
internal enum PausedTranscodeAction
{
    /// <summary>
    /// Tell the user their paused stream is about to be stopped.
    /// </summary>
    Warn,

    /// <summary>
    /// Ask the client to stop playback, which tears the FFmpeg job down through Jellyfin's
    /// normal playback-stopped path and leaves a resume point behind.
    /// </summary>
    Stop,

    /// <summary>
    /// The client did not act on the stop command, so end the FFmpeg job server-side.
    /// </summary>
    Kill
}

/// <summary>
/// One session's due action, with the timing detail its message and log line need.
/// </summary>
internal sealed class PausedTranscodeVerdict
{
    internal PausedTranscodeVerdict(
        SessionInfo session,
        PausedTranscodeAction action,
        TimeSpan pausedFor,
        int minutesUntilStop)
    {
        Session = session;
        Action = action;
        PausedFor = pausedFor;
        MinutesUntilStop = minutesUntilStop;
    }

    internal SessionInfo Session { get; }

    internal PausedTranscodeAction Action { get; }

    internal TimeSpan PausedFor { get; }

    /// <summary>
    /// Gets whole minutes left before the stop, rounded up and never below 1. Only meaningful
    /// for <see cref="PausedTranscodeAction.Warn"/>.
    /// </summary>
    internal int MinutesUntilStop { get; }
}

/// <summary>
/// Decides which paused transcodes are due to be warned, stopped, and killed.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin never evicts a paused transcode on its own. Its kill timer is driven by client
/// check-ins, and a paused client keeps checking in, so <c>IsUserPaused</c> is recorded on the job
/// and then only consulted for throttling. The FFmpeg process therefore survives - holding its
/// CUDA context, hardware frame pool, and NVENC session slot - for as long as the client is
/// willing to sit on the pause screen.
/// </para>
/// <para>
/// All timing lives here, with no Jellyfin service dependencies, so the escalation can be tested
/// against a clock the test controls.
/// </para>
/// </remarks>
internal sealed class PausedTranscodeTracker
{
    internal const int MinTimeoutMinutes = 1;
    internal const int MaxTimeoutMinutes = 1440;

    /// <summary>
    /// How long a client gets to act on the stop command before the job is killed outright.
    /// </summary>
    internal static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(10);

    // Keyed by SessionInfo instance rather than ID: Jellyfin derives session IDs from
    // client/device identifiers, so a later session can reuse the ID of one that ended.
    private readonly Dictionary<SessionInfo, TrackedSession> _tracked = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Applies the configured timeout, keeping a hand-edited configuration inside sane bounds.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The effective timeout in minutes.</returns>
    internal static int ResolveTimeoutMinutes(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return Math.Clamp(config.PausedTranscodeTimeoutMinutes, MinTimeoutMinutes, MaxTimeoutMinutes);
    }

    /// <summary>
    /// Applies the configured warning lead time. A lead that would reach back past the moment of
    /// pausing is clamped away, because a warning nobody can act on is just noise.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>Minutes before the stop at which to warn, or 0 for no warning.</returns>
    internal static int ResolveWarningMinutes(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return Math.Clamp(config.PausedTranscodeWarningMinutes, 0, ResolveTimeoutMinutes(config) - 1);
    }

    internal static string FormatWarningMessage(string template, int minutesUntilStop)
    {
        ArgumentNullException.ThrowIfNull(template);

        return template.Replace(
            "{{minutes}}",
            minutesUntilStop.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Identifies a session that is both paused and holding an FFmpeg process.
    /// </summary>
    /// <remarks>
    /// Direct play costs nothing to leave paused and is never touched. A direct stream (remux)
    /// still owns an FFmpeg process and its output files, so it counts. Live TV counts too: the
    /// nag settings' Live TV exclusion is about who gets messaged, not about which processes are
    /// allowed to sit idle.
    /// </remarks>
    /// <param name="session">The session to classify.</param>
    /// <returns>True when the session is a paused transcode.</returns>
    internal static bool IsPausedTranscode(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var playState = session.PlayState;
        if (playState == null || !playState.IsPaused)
        {
            return false;
        }

        return session.TranscodingInfo != null || playState.PlayMethod == PlayMethod.Transcode;
    }

    /// <summary>
    /// Forgets every tracked session, so a disabled or reconfigured reaper starts from a clean clock.
    /// </summary>
    internal void Reset() => _tracked.Clear();

    internal int TrackedCount => _tracked.Count;

    /// <summary>
    /// Advances the escalation for every live session and returns the actions now due.
    /// </summary>
    /// <param name="sessions">The current live sessions.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="utcNow">The current UTC time.</param>
    /// <returns>The due actions, which may be empty.</returns>
    internal IReadOnlyList<PausedTranscodeVerdict> Evaluate(
        IEnumerable<SessionInfo>? sessions,
        PluginConfiguration config,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);

        var timeout = TimeSpan.FromMinutes(ResolveTimeoutMinutes(config));
        var warningLead = TimeSpan.FromMinutes(ResolveWarningMinutes(config));
        var verdicts = new List<PausedTranscodeVerdict>();
        var stillPaused = new HashSet<SessionInfo>(ReferenceEqualityComparer.Instance);

        foreach (var session in sessions ?? Enumerable.Empty<SessionInfo>())
        {
            if (session?.Id == null
                || !IsPausedTranscode(session)
                || TranscodeGuardRules.IsUserExcluded(session.UserId, config.PausedTranscodeExcludedUserIds))
            {
                continue;
            }

            stillPaused.Add(session);

            var itemId = session.NowPlayingItem?.Id ?? Guid.Empty;
            var positionTicks = session.PlayState?.PositionTicks;

            if (!_tracked.TryGetValue(session, out var state)
                || state.ItemId != itemId
                || state.PositionTicks != positionTicks)
            {
                // First sighting, a different item, or a seek while paused. All three say someone
                // is still at the keyboard, so the clock starts now. A session that was already
                // paused before the server started therefore gets a full grace period, which is
                // the safe direction to be wrong in.
                _tracked[session] = new TrackedSession(utcNow, itemId, positionTicks);
                continue;
            }

            var pausedFor = utcNow - state.PausedSinceUtc;

            if (state.StopSentUtc.HasValue)
            {
                if (utcNow - state.StopSentUtc.Value < StopGracePeriod)
                {
                    continue;
                }

                // Still paused and still transcoding after being asked to stop: the client is not
                // going to cooperate. Forget it afterwards so a job that survives even the kill
                // waits out another full timeout instead of being hammered every tick.
                _tracked.Remove(session);
                stillPaused.Remove(session);
                verdicts.Add(new PausedTranscodeVerdict(session, PausedTranscodeAction.Kill, pausedFor, 0));
                continue;
            }

            if (pausedFor >= timeout)
            {
                state.StopSentUtc = utcNow;
                verdicts.Add(new PausedTranscodeVerdict(session, PausedTranscodeAction.Stop, pausedFor, 0));
                continue;
            }

            if (warningLead > TimeSpan.Zero && !state.WarningSent && pausedFor >= timeout - warningLead)
            {
                state.WarningSent = true;
                var remaining = timeout - pausedFor;
                verdicts.Add(new PausedTranscodeVerdict(
                    session,
                    PausedTranscodeAction.Warn,
                    pausedFor,
                    Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))));
            }
        }

        // Sessions that resumed, ended, or stopped transcoding lose their clock - and must not be
        // held alive by this dictionary.
        if (_tracked.Count != stillPaused.Count)
        {
            foreach (var tracked in _tracked.Keys.ToList())
            {
                if (!stillPaused.Contains(tracked))
                {
                    _tracked.Remove(tracked);
                }
            }
        }

        return verdicts;
    }

    private sealed class TrackedSession
    {
        internal TrackedSession(DateTime pausedSinceUtc, Guid itemId, long? positionTicks)
        {
            PausedSinceUtc = pausedSinceUtc;
            ItemId = itemId;
            PositionTicks = positionTicks;
        }

        internal DateTime PausedSinceUtc { get; }

        internal Guid ItemId { get; }

        internal long? PositionTicks { get; }

        internal bool WarningSent { get; set; }

        internal DateTime? StopSentUtc { get; set; }
    }
}
