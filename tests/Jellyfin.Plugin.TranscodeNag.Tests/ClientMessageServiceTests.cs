using Jellyfin.Plugin.TranscodeNag.Messaging;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.TranscodeNag.Tests;

public class ClientMessageServiceTests
{
    private static readonly Guid AliceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BobId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void SelectSession_MatchesOnDeviceId()
    {
        var target = TestSessions.Create("session-2", "device-2", AliceId);
        var sessions = new List<SessionInfo>
        {
            TestSessions.Create("session-1", "device-1", AliceId),
            target
        };

        Assert.Same(target, ClientMessageService.SelectSession(sessions, "device-2", AliceId, Guid.Empty));
    }

    [Fact]
    public void SelectSession_KeepsParallelSessionsOfOneUserApart()
    {
        var first = TestSessions.Create("session-1", "device-1", AliceId);
        var second = TestSessions.Create("session-2", "device-2", AliceId);
        var sessions = new List<SessionInfo> { first, second };

        Assert.Same(first, ClientMessageService.SelectSession(sessions, "device-1", AliceId, Guid.Empty));
        Assert.Same(second, ClientMessageService.SelectSession(sessions, "device-2", AliceId, Guid.Empty));
    }

    [Fact]
    public void SelectSession_NarrowsASharedDeviceToTheRequestingUser()
    {
        var alice = TestSessions.Create("session-a", "shared-device", AliceId, "alice");
        var bob = TestSessions.Create("session-b", "shared-device", BobId, "bob");
        var sessions = new List<SessionInfo> { alice, bob };

        Assert.Same(bob, ClientMessageService.SelectSession(sessions, "shared-device", BobId, Guid.Empty));
    }

    [Fact]
    public void SelectSession_RefusesAStaleSessionBelongingToAnotherUser()
    {
        // The device's only live session is somebody else's sign-in. Messaging it would show
        // one user's refusal to another, so nothing is sent.
        var sessions = new List<SessionInfo> { TestSessions.Create("session-a", "device-1", AliceId, "alice") };

        Assert.Null(ClientMessageService.SelectSession(sessions, "device-1", BobId, Guid.Empty));
    }

    [Fact]
    public void SelectSession_MatchesOnDeviceIdWhenTheRequestHasNoUser()
    {
        var only = TestSessions.Create("session-a", "device-1", AliceId, "alice");
        var sessions = new List<SessionInfo> { only };

        Assert.Same(only, ClientMessageService.SelectSession(sessions, "device-1", Guid.Empty, Guid.Empty));
    }

    [Fact]
    public void SelectSession_ReturnsNullWhenTheDeviceIsUnknown()
    {
        var sessions = new List<SessionInfo> { TestSessions.Create("session-1", "device-1", AliceId) };

        Assert.Null(ClientMessageService.SelectSession(sessions, "device-9", AliceId, Guid.Empty));
    }

    [Fact]
    public void SelectSession_WithoutADeviceIdUsesAUniqueUserMatch()
    {
        var only = TestSessions.Create("session-1", "device-1", AliceId);
        var sessions = new List<SessionInfo> { only, TestSessions.Create("session-b", "device-b", BobId, "bob") };

        Assert.Same(only, ClientMessageService.SelectSession(sessions, null, AliceId, Guid.Empty));
    }

    [Fact]
    public void SelectSession_WithoutADeviceIdRefusesToGuessBetweenUserSessions()
    {
        var sessions = new List<SessionInfo>
        {
            TestSessions.Create("session-1", "device-1", AliceId),
            TestSessions.Create("session-2", "device-2", AliceId)
        };

        // Messaging either one could warn the wrong playback, so nothing is sent.
        Assert.Null(ClientMessageService.SelectSession(sessions, null, AliceId, Guid.Empty));
    }

    [Fact]
    public void SelectSession_WithoutADeviceIdOrUserReturnsNull()
    {
        var sessions = new List<SessionInfo> { TestSessions.Create("session-1", "device-1", AliceId) };

        Assert.Null(ClientMessageService.SelectSession(sessions, null, Guid.Empty, Guid.Empty));
    }

    [Fact]
    public void SelectSession_HandlesNoSessions()
    {
        Assert.Null(ClientMessageService.SelectSession(null, "device-1", AliceId, Guid.Empty));
        Assert.Null(ClientMessageService.SelectSession(new List<SessionInfo>(), "device-1", AliceId, Guid.Empty));
    }

    [Fact]
    public async Task SendMessageAsync_NormalMessageSendsOnceWithConfiguredTimeout()
    {
        var timeoutValues = new List<int>();
        var delays = new List<TimeSpan>();
        var session = ActiveSession();
        await using var service = new ClientMessageService(
            () => new[] { session },
            (_, command, _) =>
            {
                timeoutValues.Add(Convert.ToInt32(command.TimeoutMs));
                return Task.CompletedTask;
            },
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var sent = await service.SendMessageAsync(
            session,
            new MessageCommand { Header = "header", Text = "text", TimeoutMs = 9750 },
            false,
            "test",
            "normal delivery",
            false,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.True(sent);
        Assert.Equal(new[] { 9750 }, timeoutValues);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task SendMessageAsync_StickyMessageRefreshesTwiceWithoutBlockingInitialDelivery()
    {
        var timeoutValues = new List<int>();
        var scheduledDelays = new List<TimeSpan>();
        var threeSecondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sixSecondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothDelaysScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSendCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdSendCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = ActiveSession();
        await using var service = new ClientMessageService(
            () => new[] { session },
            (_, command, _) =>
            {
                timeoutValues.Add(Convert.ToInt32(command.TimeoutMs));
                if (timeoutValues.Count == 2)
                {
                    secondSendCompleted.TrySetResult();
                }
                else if (timeoutValues.Count == 3)
                {
                    thirdSendCompleted.TrySetResult();
                }

                return Task.CompletedTask;
            },
            (delay, cancellationToken) =>
            {
                scheduledDelays.Add(delay);
                if (scheduledDelays.Count == 2)
                {
                    bothDelaysScheduled.TrySetResult();
                }

                return delay == TimeSpan.FromSeconds(3)
                    ? threeSecondGate.Task.WaitAsync(cancellationToken)
                    : sixSecondGate.Task.WaitAsync(cancellationToken);
            });

        var originalCommand = new MessageCommand { Header = "header", Text = "text", TimeoutMs = 15000 };
        var initialSend = service.SendMessageAsync(
            session,
            originalCommand,
            true,
            "test",
            "sticky delivery",
            false,
            NullLogger.Instance,
            CancellationToken.None);

        await bothDelaysScheduled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var sent = await initialSend.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(sent);
        Assert.Equal(new[] { 4000 }, timeoutValues);
        Assert.Equal(new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) }, scheduledDelays);
        Assert.Equal(15000, originalCommand.TimeoutMs);

        threeSecondGate.TrySetResult();
        await secondSendCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, timeoutValues.Count);
        Assert.False(thirdSendCompleted.Task.IsCompleted);

        sixSecondGate.TrySetResult();
        await thirdSendCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.WaitForPendingMessagesAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(new[] { 4000, 4000, 4000 }, timeoutValues);
        Assert.Equal(10000, MessageDeliveryPolicy.GetEffectiveVisibilityDurationMs(true, 15000));
    }

    [Fact]
    public async Task SendMessageAsync_NewerMessageCancelsOlderStickyRefreshes()
    {
        var sentTexts = new List<string?>();
        var delayGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = ActiveSession();
        await using var service = new ClientMessageService(
            () => new[] { session },
            (_, command, _) =>
            {
                sentTexts.Add(command.Text);
                return Task.CompletedTask;
            },
            (_, cancellationToken) => delayGate.Task.WaitAsync(cancellationToken));

        Assert.True(await service.SendMessageAsync(
            session,
            new MessageCommand { Header = "old", Text = "old sticky", TimeoutMs = 15000 },
            true,
            "test",
            "old delivery",
            false,
            NullLogger.Instance,
            CancellationToken.None));

        Assert.True(await service.SendMessageAsync(
            session,
            new MessageCommand { Header = "new", Text = "new normal", TimeoutMs = 9000 },
            false,
            "test",
            "new delivery",
            false,
            NullLogger.Instance,
            CancellationToken.None));

        delayGate.TrySetResult();
        await service.WaitForPendingMessagesAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(new[] { "old sticky", "new normal" }, sentTexts);
    }

    [Fact]
    public async Task SendMessageAsync_DoesNotRefreshAReplacementSessionWithTheSameId()
    {
        var sendCount = 0;
        var delayGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionInfo liveSession = ActiveSession();
        var originalSession = liveSession;
        await using var service = new ClientMessageService(
            () => new[] { liveSession },
            (_, _, _) =>
            {
                sendCount++;
                return Task.CompletedTask;
            },
            (_, cancellationToken) => delayGate.Task.WaitAsync(cancellationToken));

        Assert.True(await service.SendMessageAsync(
            originalSession,
            new MessageCommand { Header = "header", Text = "text", TimeoutMs = 15000 },
            true,
            "test",
            "sticky delivery",
            false,
            NullLogger.Instance,
            CancellationToken.None));

        liveSession = TestSessions.Create("session-1", "device-2", BobId, "bob");
        liveSession.AddController(new ActiveSessionController());
        delayGate.TrySetResult();

        await service.WaitForPendingMessagesAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, sendCount);
    }

    [Fact]
    public async Task SendMessageAsync_FinalRefreshStillRunsAfterTransientSecondSendFailure()
    {
        var sendCount = 0;
        var session = ActiveSession();
        await using var service = new ClientMessageService(
            () => new[] { session },
            (_, _, _) =>
            {
                sendCount++;
                return sendCount == 2
                    ? Task.FromException(new System.Net.WebSockets.WebSocketException("transient failure"))
                    : Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask);

        Assert.True(await service.SendMessageAsync(
            session,
            new MessageCommand { Header = "header", Text = "text", TimeoutMs = 15000 },
            true,
            "test",
            "sticky delivery",
            false,
            NullLogger.Instance,
            CancellationToken.None));

        await service.WaitForPendingMessagesAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(3, sendCount);
    }

    [Fact]
    public async Task CancelPendingMessages_StopsStickyRefreshesWithMatchingContext()
    {
        var sendCount = 0;
        var delayGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = ActiveSession();
        await using var service = new ClientMessageService(
            () => new[] { session },
            (_, _, _) =>
            {
                sendCount++;
                return Task.CompletedTask;
            },
            (_, cancellationToken) => delayGate.Task.WaitAsync(cancellationToken));

        Assert.True(await service.SendMessageAsync(
            session,
            new MessageCommand { Header = "header", Text = "text", TimeoutMs = 15000 },
            true,
            "test",
            "sticky delivery",
            false,
            NullLogger.Instance,
            CancellationToken.None));

        service.CancelPendingMessages(session, "test");
        delayGate.TrySetResult();
        await service.WaitForPendingMessagesAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, sendCount);
    }

    [Fact]
    public async Task CancelPendingMessages_DoesNotCancelADifferentMessageContext()
    {
        var sendCount = 0;
        var delayGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = ActiveSession();
        await using var service = new ClientMessageService(
            () => new[] { session },
            (_, _, _) =>
            {
                sendCount++;
                return Task.CompletedTask;
            },
            (_, cancellationToken) => delayGate.Task.WaitAsync(cancellationToken));

        Assert.True(await service.SendMessageAsync(
            session,
            new MessageCommand { Header = "header", Text = "text", TimeoutMs = 15000 },
            true,
            "motd",
            "sticky delivery",
            false,
            NullLogger.Instance,
            CancellationToken.None));

        service.CancelPendingMessages(session, "playback nag");
        delayGate.TrySetResult();
        await service.WaitForPendingMessagesAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(3, sendCount);
    }

    [Fact]
    public async Task ApplicationStopping_CancelsPendingStickyRefreshes()
    {
        var sendCount = 0;
        var delayGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var applicationStopping = new CancellationTokenSource();
        var session = ActiveSession();
        await using var service = new ClientMessageService(
            () => new[] { session },
            (_, _, _) =>
            {
                sendCount++;
                return Task.CompletedTask;
            },
            (_, cancellationToken) => delayGate.Task.WaitAsync(cancellationToken),
            applicationStopping.Token);

        Assert.True(await service.SendMessageAsync(
            session,
            new MessageCommand { Header = "header", Text = "text", TimeoutMs = 15000 },
            true,
            "test",
            "sticky delivery",
            false,
            NullLogger.Instance,
            CancellationToken.None));

        applicationStopping.Cancel();
        delayGate.TrySetResult();
        await service.WaitForPendingMessagesAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, sendCount);
    }

    private static SessionInfo ActiveSession()
    {
        var session = TestSessions.Create("session-1", "device-1", AliceId);
        session.AddController(new ActiveSessionController());
        return session;
    }
}
