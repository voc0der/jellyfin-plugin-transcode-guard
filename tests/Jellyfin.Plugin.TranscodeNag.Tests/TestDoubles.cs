using System.Linq;
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

/// <summary>
/// Returns a scripted sequence of readings, one per call, so concurrent admissions can be shown
/// to each take their own reading rather than sharing one.
/// </summary>
internal sealed class SequencedGpuMemoryProvider : IGpuMemoryProvider
{
    private readonly Queue<GpuMemoryQueryResult> _readings;
    private readonly GpuMemoryQueryResult _exhausted;

    public SequencedGpuMemoryProvider(params int[] freeMiBReadings)
    {
        _readings = new Queue<GpuMemoryQueryResult>(freeMiBReadings.Select(GpuMemoryQueryResult.FromFreeMiB));
        _exhausted = GpuMemoryQueryResult.Failed("no scripted reading left");
    }

    public int QueryCount { get; private set; }

    public Task<GpuMemoryQueryResult> GetFreeMemoryAsync(
        int gpuIndex,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        QueryCount++;
        lock (_readings)
        {
            return Task.FromResult(_readings.Count > 0 ? _readings.Dequeue() : _exhausted);
        }
    }
}

/// <summary>
/// Fails the way Jellyfin's raw WebSocket send path can: with an exception
/// <see cref="ClientMessageService"/> does not catch.
/// </summary>
internal sealed class ThrowingClientMessageService : IClientMessageService
{
    private readonly SessionInfo _session;

    public ThrowingClientMessageService(SessionInfo session)
    {
        _session = session;
    }

    public SessionInfo? ResolveSession(string? deviceId, Guid userId, Guid itemId) => _session;

    public Task<bool> SendMessageAsync(
        SessionInfo session,
        MessageCommand command,
        string context,
        string detail,
        bool enableLogging,
        ILogger logger,
        CancellationToken cancellationToken)
        => throw new System.Net.WebSockets.WebSocketException("the remote party closed the connection");
}

/// <summary>
/// Holds every caller inside the query until all of them have arrived, so admissions genuinely
/// overlap instead of completing one at a time as the test enumerates them.
/// </summary>
internal sealed class GatedGpuMemoryProvider : IGpuMemoryProvider
{
    private readonly int _expectedCallers;
    private readonly Queue<GpuMemoryQueryResult> _readings;
    private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public GatedGpuMemoryProvider(int expectedCallers, params int[] freeMiBReadings)
    {
        _expectedCallers = expectedCallers;
        _readings = new Queue<GpuMemoryQueryResult>(freeMiBReadings.Select(GpuMemoryQueryResult.FromFreeMiB));
    }

    public int QueryCount { get; private set; }

    public async Task<GpuMemoryQueryResult> GetFreeMemoryAsync(
        int gpuIndex,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        GpuMemoryQueryResult reading;

        lock (_readings)
        {
            QueryCount++;
            _arrived++;
            reading = _readings.Count > 0
                ? _readings.Dequeue()
                : GpuMemoryQueryResult.Failed("no scripted reading left");

            if (_arrived == _expectedCallers)
            {
                _allArrived.TrySetResult();
            }
        }

        // Times out rather than hanging if the guard ever serialises admissions.
        await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        return reading;
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
