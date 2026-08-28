using Jellyfin.Plugin.TranscodeNag.Gpu;
using Jellyfin.Plugin.TranscodeNag.Messaging;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.TranscodeNag.Tests;

/// <summary>
/// A canned free-VRAM reading, plus a count of how often it was asked for.
/// </summary>
internal sealed class FakeGpuMemoryProvider : IGpuMemoryProvider
{
    private readonly GpuMemoryQueryResult _result;

    public FakeGpuMemoryProvider(GpuMemoryQueryResult result)
    {
        _result = result;
    }

    public int QueryCount { get; private set; }

    public int? LastGpuIndex { get; private set; }

    public Task<GpuMemoryQueryResult> GetFreeMemoryAsync(
        int gpuIndex,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        QueryCount++;
        LastGpuIndex = gpuIndex;
        return Task.FromResult(_result);
    }

    public static FakeGpuMemoryProvider WithFreeMiB(int freeMiB)
        => new(GpuMemoryQueryResult.FromFreeMiB(freeMiB));

    public static FakeGpuMemoryProvider Failing(string reason)
        => new(GpuMemoryQueryResult.Failed(reason));
}

/// <summary>
/// Records the messages the guard tries to deliver and to which session.
/// </summary>
internal sealed class RecordingClientMessageService : IClientMessageService
{
    private readonly Dictionary<string, SessionInfo> _sessionsByDeviceId = new(StringComparer.Ordinal);

    public List<(SessionInfo Session, MessageCommand Command)> SentMessages { get; } = new();

    public void AddSession(SessionInfo session)
    {
        _sessionsByDeviceId[session.DeviceId] = session;
    }

    public SessionInfo? ResolveSession(string? deviceId, Guid userId, Guid itemId)
    {
        if (deviceId == null)
        {
            return null;
        }

        return _sessionsByDeviceId.TryGetValue(deviceId, out var session) ? session : null;
    }

    public Task<bool> SendMessageAsync(
        SessionInfo session,
        MessageCommand command,
        string context,
        string detail,
        bool enableLogging,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        SentMessages.Add((session, command));
        return Task.FromResult(true);
    }
}

internal static class TestSessions
{
    /// <summary>
    /// Builds a SessionInfo good enough for selection and messaging assertions.
    /// </summary>
    /// <param name="id">Session ID.</param>
    /// <param name="deviceId">Device ID.</param>
    /// <param name="userId">Owning user.</param>
    /// <param name="userName">Display name for logs.</param>
    /// <returns>The session.</returns>
    internal static SessionInfo Create(string id, string deviceId, Guid userId, string userName = "tester")
    {
        return new SessionInfo(null!, NullLogger.Instance)
        {
            Id = id,
            DeviceId = deviceId,
            DeviceName = "device-" + id,
            UserId = userId,
            UserName = userName,
            Client = "Jellyfin Web"
        };
    }
}
