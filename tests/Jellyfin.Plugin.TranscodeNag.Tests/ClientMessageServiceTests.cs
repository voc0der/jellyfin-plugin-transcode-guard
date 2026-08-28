using Jellyfin.Plugin.TranscodeNag.Messaging;
using MediaBrowser.Controller.Session;

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
}
