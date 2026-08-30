using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeNag.Messaging;

/// <summary>
/// Resolves Jellyfin sessions and delivers DisplayMessage popups to a single one of them.
/// </summary>
public interface IClientMessageService
{
    /// <summary>
    /// Finds the one session a streaming request belongs to.
    /// </summary>
    /// <remarks>
    /// Device ID is the correlation key Jellyfin itself uses for streaming requests, so parallel
    /// sessions belonging to the same user are never confused with each other. When the request
    /// carries no device ID, a user match is only used if it is unambiguous.
    /// </remarks>
    /// <param name="deviceId">The device ID from the streaming request.</param>
    /// <param name="userId">The authenticated user, or <see cref="Guid.Empty"/> if unknown.</param>
    /// <param name="itemId">The item being requested, used to break ties.</param>
    /// <returns>The matching session, or null when it cannot be identified unambiguously.</returns>
    SessionInfo? ResolveSession(string? deviceId, Guid userId, Guid itemId);

    /// <summary>
    /// Cancels pending message delivery for a session, optionally only when its context matches.
    /// </summary>
    /// <param name="session">The session whose pending delivery should stop.</param>
    /// <param name="context">Optional delivery context to match; null cancels any pending message.</param>
    void CancelPendingMessages(SessionInfo session, string? context = null);

    /// <summary>
    /// Sends a message command to one session, logging the same delivery diagnostics for every caller.
    /// </summary>
    /// <param name="session">Target session.</param>
    /// <param name="command">The message to display.</param>
    /// <param name="useStickyMessages">Whether to refresh the message for clients that dismiss it early.</param>
    /// <param name="context">Short label for logs, for example "playback nag".</param>
    /// <param name="detail">Extra log detail for this specific send.</param>
    /// <param name="enableLogging">Whether informational delivery logging is switched on.</param>
    /// <param name="logger">The caller's logger, so log categories stay with the feature that sent the message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True only when Jellyfin accepted the command for a live controller.</returns>
    Task<bool> SendMessageAsync(
        SessionInfo session,
        MessageCommand command,
        bool useStickyMessages,
        string context,
        string detail,
        bool enableLogging,
        ILogger logger,
        CancellationToken cancellationToken);
}
