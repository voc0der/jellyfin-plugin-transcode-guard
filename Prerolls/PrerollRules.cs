using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.TranscodeGuard.Prerolls;

/// <summary>
/// One pre-roll the user could be shown, with what Jellyfin knows about them having seen it.
/// </summary>
/// <param name="Id">The pre-roll's library item id.</param>
/// <param name="Seen">True once the user has started it at least once.</param>
/// <param name="LastSeen">When the user last started it, or null if never.</param>
internal sealed record PrerollCandidate(Guid Id, bool Seen, DateTime? LastSeen);

/// <summary>
/// The pre-roll decisions that need no Jellyfin services, kept apart so every combination of
/// settings can be tested directly.
/// </summary>
internal static class PrerollRules
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".ts", ".m2ts", ".wmv", ".mpg", ".mpeg"
    };

    internal static bool IsVideoFile(string? path)
    {
        return !string.IsNullOrEmpty(path) && VideoExtensions.Contains(Path.GetExtension(path));
    }

    /// <summary>
    /// Lists the folder's video files in file-name order, which is the order "in order" means.
    /// Hidden files (macOS "._" sidecars and dotfiles) are skipped. A missing or unreadable folder
    /// is an empty list, never an exception on the playback path.
    /// </summary>
    /// <param name="folder">The configured pre-roll folder.</param>
    /// <returns>Full paths, sorted by file name.</returns>
    internal static IReadOnlyList<string> ListPrerollFiles(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return Array.Empty<string>();
        }

        try
        {
            var root = Path.GetFullPath(folder.Trim());
            if (!Directory.Exists(root))
            {
                return Array.Empty<string>();
            }

            return Directory.EnumerateFiles(root)
                .Where(path => IsVideoFile(path) && !Path.GetFileName(path).StartsWith('.'))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Tells whether a path is inside the pre-roll folder, so a pre-roll never gets pre-rolls of
    /// its own and is never nagged about as if the user had picked it.
    /// </summary>
    /// <param name="path">The item's path.</param>
    /// <param name="folder">The configured pre-roll folder.</param>
    /// <returns>True when the path is inside the folder.</returns>
    internal static bool IsInFolder(string? path, string? folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim())) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Rolls the configured chance.
    /// </summary>
    /// <param name="chancePercent">The configured chance, 0-100. Out-of-range values are clamped.</param>
    /// <param name="roll">A uniform roll in [0, 100).</param>
    /// <returns>True when the playback gets pre-rolls.</returns>
    internal static bool PassesChance(int chancePercent, int roll)
    {
        return roll < Math.Clamp(chancePercent, 0, 100);
    }

    /// <summary>
    /// Tells whether a session that has already had pre-rolls <paramref name="shown"/> times may
    /// have them again.
    /// </summary>
    /// <param name="shown">How many times the session has had pre-rolls.</param>
    /// <param name="limit">The configured limit. Below 1 means no limit.</param>
    /// <returns>True when the session is under its limit.</returns>
    internal static bool IsUnderSessionLimit(int shown, int limit)
    {
        return limit < 1 || shown < limit;
    }

    /// <summary>
    /// Picks which pre-rolls play, from candidates in file-name order.
    /// </summary>
    /// <param name="candidates">Every available pre-roll, in file-name order.</param>
    /// <param name="oncePerUser">Drop the pre-rolls the user has already seen.</param>
    /// <param name="playAll">Play every remaining pre-roll back to back instead of one.</param>
    /// <param name="random">Shuffle (play all) or pick at random (one) instead of following the order.</param>
    /// <param name="rng">Source of randomness.</param>
    /// <returns>The pre-rolls to play, in play order. Empty when nothing is left to show.</returns>
    internal static IReadOnlyList<PrerollCandidate> Pick(
        IReadOnlyList<PrerollCandidate> candidates,
        bool oncePerUser,
        bool playAll,
        bool random,
        Random rng)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rng);

        var pool = oncePerUser ? candidates.Where(candidate => !candidate.Seen).ToList() : candidates.ToList();
        if (pool.Count == 0)
        {
            return Array.Empty<PrerollCandidate>();
        }

        if (playAll)
        {
            if (random)
            {
                // Fisher-Yates, so every order is equally likely.
                for (var i = pool.Count - 1; i > 0; i--)
                {
                    var j = rng.Next(i + 1);
                    (pool[i], pool[j]) = (pool[j], pool[i]);
                }
            }

            return pool;
        }

        if (random)
        {
            return new[] { pool[rng.Next(pool.Count)] };
        }

        // In order, one at a time: the one this user saw longest ago, never-seen first, so the
        // list is walked through rather than the first file replaying forever. OrderBy is stable,
        // so ties keep file-name order.
        return new[] { pool.OrderBy(candidate => candidate.LastSeen ?? DateTime.MinValue).First() };
    }
}
