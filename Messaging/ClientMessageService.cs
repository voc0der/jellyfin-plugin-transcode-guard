using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeNag.Messaging;

/// <summary>
/// The shared session-resolution and DisplayMessage plumbing used by every notification the plugin sends.
/// </summary>
public sealed class ClientMessageService : IClientMessageService
{
    private readonly ISessionManager _sessionManager;

    public ClientMessageService(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <inheritdoc />
    public SessionInfo? ResolveSession(string? deviceId, Guid userId, Guid itemId)
        => SelectSession(_sessionManager.Sessions, deviceId, userId, itemId);

    /// <summary>
    /// The session-selection rules, separated from <see cref="ISessionManager"/> so they can be tested directly.
    /// </summary>
    /// <param name="sessions">The live sessions to choose from.</param>
    /// <param name="deviceId">The device ID from the streaming request.</param>
    /// <param name="userId">The authenticated user, or <see cref="Guid.Empty"/> if unknown.</param>
    /// <param name="itemId">The item being requested, used to break ties.</param>
    /// <returns>The matching session, or null when it cannot be identified unambiguously.</returns>
    internal static SessionInfo? SelectSession(
        IEnumerable<SessionInfo>? sessions,
        string? deviceId,
        Guid userId,
        Guid itemId)
    {
        if (sessions == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var deviceMatches = sessions
                .Where(session => session.Id != null
                    && string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal))
                .ToList();

            // A device can carry a stale session for whoever signed in previously, so the
            // requesting user must match too. No match means no message rather than a message
            // to somebody else's session.
            if (userId != Guid.Empty)
            {
                deviceMatches = deviceMatches.Where(session => SessionBelongsToUser(session, userId)).ToList();
            }

            return SelectBestMatch(deviceMatches, itemId);
        }

        if (userId == Guid.Empty)
        {
            return null;
        }

        // No device ID to correlate on. Only message a user session when there is exactly one,
        // so a second session of the same user can never receive another session's warning.
        var candidates = sessions
            .Where(session => session.Id != null && SessionBelongsToUser(session, userId))
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <inheritdoc />
    public async Task<bool> SendMessageAsync(
        SessionInfo session,
        MessageCommand command,
        string context,
        string detail,
        bool enableLogging,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(logger);

        if (session.Id == null)
        {
            return false;
        }

        LogMessageDeliveryDiagnostics(session, context, detail, enableLogging, logger);

        // Jellyfin treats sending to an empty controller collection as a successful no-op.
        // Do not report or persist that as a delivered message.
        var (_, activeControllerCount, _) = GetSessionControllerStats(session);
        if (activeControllerCount == 0)
        {
            return false;
        }

        try
        {
            await _sessionManager.SendMessageCommand(
                null,
                session.Id,
                command,
                cancellationToken).ConfigureAwait(false);

            if (enableLogging)
            {
                logger.LogInformation(
                    "Completed {Context} message send to session {SessionId}",
                    context,
                    session.Id);
            }

            return true;
        }
        catch (ResourceNotFoundException ex)
        {
            logger.LogError(ex, "Error sending {Context} message to session {SessionId}", context, session.Id);
        }
        catch (ObjectDisposedException ex)
        {
            logger.LogError(ex, "Error sending {Context} message to session {SessionId}", context, session.Id);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Error sending {Context} message to session {SessionId}", context, session.Id);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Error sending {Context} message to session {SessionId}", context, session.Id);
        }
        catch (ArgumentException ex)
        {
            logger.LogError(ex, "Error sending {Context} message to session {SessionId}", context, session.Id);
        }

        return false;
    }

    private static bool SessionBelongsToUser(SessionInfo session, Guid userId)
    {
        return session.UserId == userId || session.ContainsUser(userId);
    }

    private static SessionInfo? SelectBestMatch(List<SessionInfo> candidates, Guid itemId)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        // Several live sessions share the device (for example a client that reconnected).
        // Prefer the one already playing this item; otherwise take the first.
        if (itemId != Guid.Empty)
        {
            var playingMatch = candidates.FirstOrDefault(session => session.NowPlayingItem?.Id == itemId);
            if (playingMatch != null)
            {
                return playingMatch;
            }
        }

        return candidates[0];
    }

    private static void LogMessageDeliveryDiagnostics(
        SessionInfo session,
        string context,
        string detail,
        bool enableLogging,
        ILogger logger)
    {
        if (!enableLogging)
        {
            return;
        }

        var (controllerCount, activeControllerCount, mediaControlControllerCount) = GetSessionControllerStats(session);
        var supportedCommands = FormatSupportedCommands(session.SupportedCommands);
        var supportsDisplayMessage = session.SupportedCommands?.Contains(GeneralCommandType.DisplayMessage) == true;

        logger.LogInformation(
            "Sending {Context} message to session {SessionId} ({Client} {ApplicationVersion}) for user {UserName} on device {DeviceName} ({DeviceId}) - {Detail}; Controllers: {ControllerCount} total, {ActiveControllerCount} active, {MediaControlControllerCount} media-control; SupportsRemoteControl: {SupportsRemoteControl}; SupportsMediaControl: {SupportsMediaControl}; SupportsDisplayMessage: {SupportsDisplayMessage}; SupportedCommands: {SupportedCommands}",
            context,
            session.Id ?? "Unknown",
            session.Client ?? "Unknown",
            session.ApplicationVersion ?? "Unknown",
            session.UserName ?? "Unknown",
            session.DeviceName ?? "Unknown",
            session.DeviceId ?? "Unknown",
            detail,
            controllerCount,
            activeControllerCount,
            mediaControlControllerCount,
            session.SupportsRemoteControl,
            session.SupportsMediaControl,
            supportsDisplayMessage,
            supportedCommands);

        if (controllerCount == 0 || activeControllerCount == 0)
        {
            logger.LogWarning(
                "{Context} target session {SessionId} has {ControllerCount} controller(s) and {ActiveControllerCount} active controller(s). Jellyfin may accept the command without any client receiving a popup; check WebSocket/reverse proxy/client session state.",
                context,
                session.Id ?? "Unknown",
                controllerCount,
                activeControllerCount);
        }
        else if (!supportsDisplayMessage)
        {
            logger.LogWarning(
                "{Context} target session {SessionId} does not advertise DisplayMessage support. The client may ignore the nag popup.",
                context,
                session.Id ?? "Unknown");
        }
    }

    private static (int ControllerCount, int ActiveControllerCount, int MediaControlControllerCount) GetSessionControllerStats(SessionInfo session)
    {
        var controllers = session.SessionControllers;
        if (controllers == null)
        {
            return (0, 0, 0);
        }

        return (
            controllers.Count,
            controllers.Count(controller => controller.IsSessionActive),
            controllers.Count(controller => controller.SupportsMediaControl));
    }

    private static string FormatSupportedCommands(IReadOnlyList<GeneralCommandType>? supportedCommands)
    {
        if (supportedCommands == null || supportedCommands.Count == 0)
        {
            return "None";
        }

        return string.Join(", ", supportedCommands);
    }
}
