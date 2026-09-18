using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeGuard.Messaging;

/// <summary>
/// The shared session-resolution and DisplayMessage plumbing used by every notification the plugin sends.
/// </summary>
public sealed class ClientMessageService : IClientMessageService, IDisposable, IAsyncDisposable
{
    private readonly Func<IEnumerable<SessionInfo>> _sessionsAccessor;
    private readonly Func<string, MessageCommand, IReadOnlyDictionary<string, string>?, CancellationToken, Task> _commandSender;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<SessionInfo, SessionDeliveryState> _deliveryStates = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DeliveryRegistration> _activeDeliveries = new();
    private readonly object _deliveryLock = new();
    private readonly CancellationTokenRegistration _applicationStoppingRegistration;
    private bool _stopping;

    public ClientMessageService(
        ISessionManager sessionManager,
        IHostApplicationLifetime applicationLifetime)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(applicationLifetime);

        _sessionsAccessor = () => sessionManager.Sessions;
        _commandSender = (sessionId, command, extraArguments, cancellationToken) => SendMessageCommandAsync(
            sessionManager,
            sessionId,
            command,
            extraArguments,
            cancellationToken);
        _delay = Task.Delay;
        _applicationStoppingRegistration = applicationLifetime.ApplicationStopping.Register(CancelAllPendingMessages);
    }

    internal ClientMessageService(
        Func<IEnumerable<SessionInfo>> sessionsAccessor,
        Func<string, MessageCommand, CancellationToken, Task> commandSender,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken applicationStopping = default)
        : this(
            sessionsAccessor,
            WithoutExtraArguments(commandSender ?? throw new ArgumentNullException(nameof(commandSender))),
            delay,
            applicationStopping)
    {
    }

    internal ClientMessageService(
        Func<IEnumerable<SessionInfo>> sessionsAccessor,
        Func<string, MessageCommand, IReadOnlyDictionary<string, string>?, CancellationToken, Task> commandSender,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken applicationStopping = default)
    {
        _sessionsAccessor = sessionsAccessor ?? throw new ArgumentNullException(nameof(sessionsAccessor));
        _commandSender = commandSender ?? throw new ArgumentNullException(nameof(commandSender));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _applicationStoppingRegistration = applicationStopping.Register(CancelAllPendingMessages);
    }

    private static Func<string, MessageCommand, IReadOnlyDictionary<string, string>?, CancellationToken, Task> WithoutExtraArguments(
        Func<string, MessageCommand, CancellationToken, Task> commandSender)
        => (sessionId, command, _, cancellationToken) => commandSender(sessionId, command, cancellationToken);

    private static Task SendMessageCommandAsync(
        ISessionManager sessionManager,
        string sessionId,
        MessageCommand command,
        IReadOnlyDictionary<string, string>? extraArguments,
        CancellationToken cancellationToken)
    {
        // Plain messages keep going through Jellyfin's own conversion. Only a message that carries
        // extra arguments is built here, because MessageCommand has nowhere to put them.
        if (extraArguments == null || extraArguments.Count == 0)
        {
            return sessionManager.SendMessageCommand(null, sessionId, command, cancellationToken);
        }

        return sessionManager.SendGeneralCommand(
            null,
            sessionId,
            BuildDisplayMessageCommand(command, extraArguments),
            cancellationToken);
    }

    /// <summary>
    /// Builds the DisplayMessage command exactly as Jellyfin's <c>SendMessageCommand</c> does
    /// (identical in 12.0 and 12.1), then adds the extra arguments.
    /// </summary>
    /// <param name="command">The message to display.</param>
    /// <param name="extraArguments">Additional arguments; Header, Text, and TimeoutMs cannot be overridden.</param>
    /// <returns>The general command to send.</returns>
    internal static GeneralCommand BuildDisplayMessageCommand(
        MessageCommand command,
        IReadOnlyDictionary<string, string>? extraArguments)
    {
        ArgumentNullException.ThrowIfNull(command);

        var generalCommand = new GeneralCommand
        {
            Name = GeneralCommandType.DisplayMessage
        };

        generalCommand.Arguments["Header"] = command.Header;
        generalCommand.Arguments["Text"] = command.Text;

        if (command.TimeoutMs.HasValue)
        {
            generalCommand.Arguments["TimeoutMs"] = command.TimeoutMs.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (extraArguments != null)
        {
            foreach (var (name, value) in extraArguments)
            {
                // The standard arguments decide what every client shows - an absent TimeoutMs, for
                // one, turns Jellyfin Web's toast into a blocking alert - so extras can only add.
                if (!IsStandardDisplayMessageArgument(name))
                {
                    generalCommand.Arguments.TryAdd(name, value);
                }
            }
        }

        return generalCommand;
    }

    private static bool IsStandardDisplayMessageArgument(string name)
        => string.Equals(name, "Header", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Text", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "TimeoutMs", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public SessionInfo? ResolveSession(string? deviceId, Guid userId, Guid itemId)
        => SelectSession(_sessionsAccessor(), deviceId, userId, itemId);

    /// <inheritdoc />
    public void CancelPendingMessages(SessionInfo session, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        DeliveryRegistration? pending = null;
        lock (_deliveryLock)
        {
            if (_deliveryStates.TryGetValue(session, out var state)
                && (context == null
                    || string.Equals(state.CurrentDelivery?.Context, context, StringComparison.Ordinal)))
            {
                pending = state.CurrentDelivery;
                if (context == null)
                {
                    _deliveryStates.Remove(session);
                }
                else
                {
                    state.CurrentDelivery = null;
                }
            }
        }

        pending?.Cancel();
    }

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
    public Task<bool> SendMessageAsync(
        SessionInfo session,
        MessageCommand command,
        bool useStickyMessages,
        string context,
        string detail,
        bool enableLogging,
        ILogger logger,
        CancellationToken cancellationToken)
        => SendMessageAsync(
            session,
            command,
            null,
            useStickyMessages,
            context,
            detail,
            enableLogging,
            logger,
            cancellationToken);

    /// <inheritdoc />
    public async Task<bool> SendMessageAsync(
        SessionInfo session,
        MessageCommand command,
        IReadOnlyDictionary<string, string>? extraArguments,
        bool useStickyMessages,
        string context,
        string detail,
        bool enableLogging,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(logger);

        if (session.Id == null)
        {
            return false;
        }

        if (!TryBeginDelivery(session, context, out var state, out var delivery))
        {
            return false;
        }

        var commandToSend = useStickyMessages
            ? new MessageCommand
            {
                Header = command.Header,
                Text = command.Text,
                TimeoutMs = MessageDeliveryPolicy.StickyMessageTimeoutMs
            }
            : command;
        var gateEntered = false;
        var refreshOwnsDelivery = false;

        try
        {
            using var initialSendCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                delivery.CancellationToken);

            await state.SendGate.WaitAsync(initialSendCancellation.Token).ConfigureAwait(false);
            gateEntered = true;

            // A newer message can supersede this one while it is waiting behind another send.
            // In that case, never let the older initial command overtake the newer delivery.
            if (!IsCurrentDelivery(session, state, delivery))
            {
                return false;
            }

            LogMessageDeliveryDiagnostics(session, context, detail, enableLogging, logger);

            // Jellyfin treats sending to an empty controller collection as a successful no-op.
            // Do not report or persist that as a delivered message.
            if (GetSessionControllerStats(session).ActiveControllerCount == 0)
            {
                return false;
            }

            await _commandSender(
                session.Id,
                commandToSend,
                extraArguments,
                initialSendCancellation.Token).ConfigureAwait(false);

            if (enableLogging)
            {
                logger.LogInformation(
                    "Completed {Context} message send to session {SessionId}",
                    context,
                    session.Id);
            }

            if (useStickyMessages)
            {
                refreshOwnsDelivery = TryStartStickyRefreshes(
                    session,
                    state,
                    delivery,
                    commandToSend,
                    extraArguments,
                    context,
                    enableLogging,
                    logger);
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
            if (delivery.CancellationToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                if (enableLogging)
                {
                    logger.LogDebug(
                        ex,
                        "Canceled superseded {Context} message delivery to session {SessionId}",
                        context,
                        session.Id);
                }
            }
            else
            {
                logger.LogError(ex, "Error sending {Context} message to session {SessionId}", context, session.Id);
            }
        }
        catch (ArgumentException ex)
        {
            logger.LogError(ex, "Error sending {Context} message to session {SessionId}", context, session.Id);
        }

        finally
        {
            if (gateEntered)
            {
                state.SendGate.Release();
            }

            if (!refreshOwnsDelivery)
            {
                CompleteDelivery(session, state, delivery);
            }
        }

        return false;
    }

    private bool TryStartStickyRefreshes(
        SessionInfo session,
        SessionDeliveryState state,
        DeliveryRegistration delivery,
        MessageCommand command,
        IReadOnlyDictionary<string, string>? extraArguments,
        string context,
        bool enableLogging,
        ILogger logger)
    {
        if (!IsCurrentDelivery(session, state, delivery))
        {
            return false;
        }

        // The first delivery determines SendMessageAsync's result. Refreshes remain asynchronous
        // so a GPU refusal does not hold its HTTP response open for the visibility window.
        _ = RepeatStickyMessageAsync(session, state, delivery, command, extraArguments, context, enableLogging, logger);
        return true;
    }

    private async Task RepeatStickyMessageAsync(
        SessionInfo session,
        SessionDeliveryState state,
        DeliveryRegistration delivery,
        MessageCommand command,
        IReadOnlyDictionary<string, string>? extraArguments,
        string context,
        bool enableLogging,
        ILogger logger)
    {
        try
        {
            var secondSend = SendStickyRefreshAfterDelayAsync(
                session,
                state,
                delivery,
                command,
                extraArguments,
                context,
                2,
                enableLogging,
                logger);
            var thirdSend = SendStickyRefreshAfterDelayAsync(
                session,
                state,
                delivery,
                command,
                extraArguments,
                context,
                3,
                enableLogging,
                logger);

            await Task.WhenAll(secondSend, thirdSend).ConfigureAwait(false);
        }
        finally
        {
            CompleteDelivery(session, state, delivery);
        }
    }

    private async Task SendStickyRefreshAfterDelayAsync(
        SessionInfo session,
        SessionDeliveryState state,
        DeliveryRegistration delivery,
        MessageCommand command,
        IReadOnlyDictionary<string, string>? extraArguments,
        string context,
        int sendNumber,
        bool enableLogging,
        ILogger logger)
    {
        var gateEntered = false;
        try
        {
            await _delay(
                MessageDeliveryPolicy.GetStickyRefreshDelay(sendNumber),
                delivery.CancellationToken).ConfigureAwait(false);

            await state.SendGate.WaitAsync(delivery.CancellationToken).ConfigureAwait(false);
            gateEntered = true;

            if (!IsCurrentDelivery(session, state, delivery)
                || !IsSameLiveSession(session)
                || session.Id == null
                || GetSessionControllerStats(session).ActiveControllerCount == 0)
            {
                if (enableLogging && !delivery.CancellationToken.IsCancellationRequested)
                {
                    logger.LogInformation(
                        "Stopped refreshing sticky {Context} message because session {SessionId} is no longer the same live session with an active controller",
                        context,
                        session.Id ?? "Unknown");
                }

                return;
            }

            await _commandSender(session.Id, command, extraArguments, delivery.CancellationToken).ConfigureAwait(false);

            if (enableLogging)
            {
                logger.LogInformation(
                    "Completed sticky {Context} message refresh {SendNumber} of {SendCount} to session {SessionId}",
                    context,
                    sendNumber,
                    MessageDeliveryPolicy.StickyMessageSendCount,
                    session.Id);
            }
        }
        catch (OperationCanceledException) when (delivery.CancellationToken.IsCancellationRequested)
        {
            // A newer message, session end, or host shutdown superseded this refresh.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Each refresh owns its error boundary, so a transient failure at t=3 does not
            // suppress the final compatibility attempt at t=6.
            logger.LogError(
                ex,
                "Error sending sticky {Context} message refresh {SendNumber} of {SendCount} to session {SessionId}",
                context,
                sendNumber,
                MessageDeliveryPolicy.StickyMessageSendCount,
                session.Id);
        }
        finally
        {
            if (gateEntered)
            {
                state.SendGate.Release();
            }
        }
    }

    private bool TryBeginDelivery(
        SessionInfo session,
        string context,
        out SessionDeliveryState state,
        out DeliveryRegistration delivery)
    {
        DeliveryRegistration? superseded = null;
        lock (_deliveryLock)
        {
            if (_stopping)
            {
                state = null!;
                delivery = null!;
                return false;
            }

            if (!_deliveryStates.TryGetValue(session, out state!))
            {
                state = new SessionDeliveryState();
                _deliveryStates.Add(session, state);
            }

            superseded = state.CurrentDelivery;

            delivery = new DeliveryRegistration(context);
            state.CurrentDelivery = delivery;
            _activeDeliveries.Add(delivery);
        }

        superseded?.Cancel();
        return true;
    }

    private bool IsCurrentDelivery(
        SessionInfo session,
        SessionDeliveryState state,
        DeliveryRegistration delivery)
    {
        lock (_deliveryLock)
        {
            return !_stopping
                && _deliveryStates.TryGetValue(session, out var currentState)
                && ReferenceEquals(currentState, state)
                && ReferenceEquals(state.CurrentDelivery, delivery);
        }
    }

    private bool IsSameLiveSession(SessionInfo session)
        => _sessionsAccessor().Any(candidate => ReferenceEquals(candidate, session));

    private void CompleteDelivery(
        SessionInfo session,
        SessionDeliveryState state,
        DeliveryRegistration delivery)
    {
        lock (_deliveryLock)
        {
            _activeDeliveries.Remove(delivery);
            if (_deliveryStates.TryGetValue(session, out var currentState)
                && ReferenceEquals(currentState, state)
                && ReferenceEquals(state.CurrentDelivery, delivery))
            {
                state.CurrentDelivery = null;
            }
        }

        delivery.Complete();
    }

    private void CancelAllPendingMessages()
    {
        var pending = PrepareToStop();
        foreach (var delivery in pending)
        {
            delivery.Cancel();
        }
    }

    private List<DeliveryRegistration> PrepareToStop()
    {
        lock (_deliveryLock)
        {
            _stopping = true;

            var pending = _activeDeliveries.ToList();
            _deliveryStates.Clear();
            return pending;
        }
    }

    internal Task WaitForPendingMessagesAsync()
    {
        lock (_deliveryLock)
        {
            return Task.WhenAll(_activeDeliveries.Select(delivery => delivery.Completion));
        }
    }

    public void Dispose()
    {
        var pending = PrepareToStop();
        foreach (var delivery in pending)
        {
            delivery.Cancel();
        }

        _applicationStoppingRegistration.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        var pending = PrepareToStop();
        foreach (var delivery in pending)
        {
            delivery.Cancel();
        }

        await Task.WhenAll(pending.Select(delivery => delivery.Completion)).ConfigureAwait(false);
        _applicationStoppingRegistration.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class SessionDeliveryState
    {
        internal SemaphoreSlim SendGate { get; } = new(1, 1);

        internal DeliveryRegistration? CurrentDelivery { get; set; }
    }

    private sealed class DeliveryRegistration
    {
        // Cancellation can be requested concurrently by supersession, session end, and host
        // shutdown. This source is not linked to a long-lived token and no WaitHandle is created,
        // so letting it become collectible avoids racing Dispose against Cancel.
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal DeliveryRegistration(string context)
        {
            Context = context;
        }

        internal string Context { get; }

        internal CancellationToken CancellationToken => _cancellation.Token;

        internal Task Completion => _completion.Task;

        internal void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Cancellation callbacks are third-party code. A broken callback must not stop
                // session cleanup, supersession, or host shutdown from canceling other deliveries.
            }
        }

        internal void Complete()
        {
            _completion.TrySetResult();
        }
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
