using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeGuard.Prerolls;

/// <summary>
/// Offers the videos in the configured folder as intros. Clients that support Jellyfin's cinema
/// mode ask for intros before starting a video and play what comes back first, so nothing here
/// runs on a timer: the work happens only when a client asks.
/// </summary>
/// <remarks>
/// Jellyfin creates every <see cref="IIntroProvider"/> in the loaded assemblies itself. Each file
/// becomes a library item with no parent folder, the same way other local-intro plugins do it,
/// because Jellyfin only plays intros that exist in its database. Having no parent keeps the item
/// out of every library, search, and home row. "Seen" is Jellyfin's own per-user play count on
/// that item, which every client writes when it reports the pre-roll starting.
/// </remarks>
public sealed class PrerollIntroProvider : IIntroProvider, IDisposable
{
    private readonly PrerollVideos _videos;
    private readonly IUserDataManager _userDataManager;
    private readonly ISessionManager _sessionManager;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<PrerollIntroProvider> _logger;
    private readonly PrerollSessionCounter _sessions = new();

    public PrerollIntroProvider(
        PrerollVideos videos,
        IUserDataManager userDataManager,
        ISessionManager sessionManager,
        IAuthorizationContext authorizationContext,
        ILogger<PrerollIntroProvider> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _videos = videos;
        _userDataManager = userDataManager;
        _sessionManager = sessionManager;
        _authorizationContext = authorizationContext;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;

        // The per-session limit starts over with the session, like the MOTD's once per session.
        _sessionManager.SessionEnded += OnSessionEnded;
    }

    public string Name => "Transcode Guard Pre-rolls";

    public async Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
    {
        try
        {
            return await GetIntrosCoreAsync(item, user).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A broken pre-roll must never stand between the user and the video they picked.
            _logger.LogError(ex, "Could not pick pre-rolls for {ItemName}; playing it without them", item?.Name);
            return Array.Empty<IntroInfo>();
        }
    }

    public void Dispose()
    {
        _sessionManager.SessionEnded -= OnSessionEnded;
    }

    private async Task<IEnumerable<IntroInfo>> GetIntrosCoreAsync(BaseItem item, User user)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnablePrerolls || item == null || user == null)
        {
            return Array.Empty<IntroInfo>();
        }

        if (item.MediaType != MediaType.Video || PrerollRules.IsInFolder(item.Path, config.PrerollFolder)
            || PrerollRules.IsLimitClip(item.Path, config))
        {
            return Array.Empty<IntroInfo>();
        }

        if (TranscodeGuardRules.IsUserExcluded(user.Id, config.PrerollExcludedUserIds))
        {
            LogSkip(config, "user {UserName} is excluded", user.Username);
            return Array.Empty<IntroInfo>();
        }

        var request = await GetRequestAuthorizationAsync().ConfigureAwait(false);
        var client = request?.Client;
        if (!TranscodeGuardRules.IsClientAllowed(client, config.PrerollIncludedClientPatterns, config.PrerollExcludedClientPatterns))
        {
            LogSkip(config, "client {Client} is filtered out", client ?? "Unknown");
            return Array.Empty<IntroInfo>();
        }

        var sessionKey = PrerollSessionCounter.Key(user.Id, request?.DeviceId);
        if (!_sessions.CanShow(sessionKey, config.PrerollSessionLimit))
        {
            LogSkip(config, "this session already had its {Limit} pre-roll(s)", config.PrerollSessionLimit);
            return Array.Empty<IntroInfo>();
        }

        if (!PrerollRules.PassesChance(config.PrerollChancePercent, Random.Shared.Next(100)))
        {
            LogSkip(config, "the {Chance}% chance did not come up", config.PrerollChancePercent);
            return Array.Empty<IntroInfo>();
        }

        var candidates = PrerollRules.ListPrerollFiles(config.PrerollFolder)
            .Where(path => !PrerollRules.IsLimitClip(path, config))
            .Select(_videos.GetOrCreateItem)
            .Where(video => video.IsVisibleStandalone(user))
            .Select(video => ToCandidate(user, video))
            .ToList();

        var picked = PrerollRules.Pick(candidates, config.PrerollOncePerUser, config.PrerollPlayAll, config.PrerollRandom, Random.Shared);
        if (picked.Count == 0)
        {
            LogSkip(config, "no pre-roll is left to show {UserName}", user.Username);
            return Array.Empty<IntroInfo>();
        }

        // Checked again under the lock: two requests from one session must not both get through.
        if (!_sessions.TryRecordShow(sessionKey, config.PrerollSessionLimit))
        {
            return Array.Empty<IntroInfo>();
        }

        if (config.EnableLogging)
        {
            _logger.LogInformation(
                "Playing {Count} pre-roll(s) for {UserName} on {Client} before {ItemName}",
                picked.Count,
                user.Username,
                client ?? "Unknown",
                item.Name);
        }

        return picked.Select(candidate => new IntroInfo { ItemId = candidate.Id }).ToArray();
    }

    private PrerollCandidate ToCandidate(User user, Video video)
    {
        var data = _userDataManager.GetUserData(user, video);
        return new PrerollCandidate(
            video.Id,
            Seen: data != null && (data.PlayCount > 0 || data.Played),
            LastSeen: data?.LastPlayedDate);
    }

    private async Task<AuthorizationInfo?> GetRequestAuthorizationAsync()
    {
        var context = _httpContextAccessor?.HttpContext;
        if (context == null)
        {
            return null;
        }

        try
        {
            return await _authorizationContext.GetAuthorizationInfo(context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Could not read the client of a pre-roll request");
            return null;
        }
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        if (e.SessionInfo != null)
        {
            _sessions.Forget(PrerollSessionCounter.Key(e.SessionInfo.UserId, e.SessionInfo.DeviceId));
        }
    }

    private void LogSkip<T>(PluginConfiguration config, string reason, T argument)
    {
        if (config.EnableLogging)
        {
#pragma warning disable CA2254 // The reason is one of the fixed templates above.
            _logger.LogInformation("Skipping pre-rolls: " + reason, argument);
#pragma warning restore CA2254
        }
    }
}

/// <summary>
/// Counts how many times each session has had pre-rolls. Kept in memory: a session does not
/// outlive the server, so neither does its count.
/// </summary>
internal sealed class PrerollSessionCounter
{
    private readonly Dictionary<string, int> _shown = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    internal static string Key(Guid userId, string? deviceId)
    {
        return userId.ToString("N") + "|" + (deviceId ?? string.Empty);
    }

    internal bool CanShow(string key, int limit)
    {
        lock (_gate)
        {
            return PrerollRules.IsUnderSessionLimit(_shown.GetValueOrDefault(key), limit);
        }
    }

    internal bool TryRecordShow(string key, int limit)
    {
        lock (_gate)
        {
            var shown = _shown.GetValueOrDefault(key);
            if (!PrerollRules.IsUnderSessionLimit(shown, limit))
            {
                return false;
            }

            _shown[key] = shown + 1;
            return true;
        }
    }

    internal void Forget(string key)
    {
        lock (_gate)
        {
            _shown.Remove(key);
        }
    }
}
