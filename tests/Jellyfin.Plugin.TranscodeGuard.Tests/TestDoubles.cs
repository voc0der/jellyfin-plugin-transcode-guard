using System.Linq;
using Jellyfin.Plugin.TranscodeGuard.Gpu;
using Jellyfin.Plugin.TranscodeGuard.Messaging;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

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

    public List<(SessionInfo Session, MessageCommand Command, bool UseStickyMessages)> SentMessages { get; } = new();

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

    public void CancelPendingMessages(SessionInfo session, string? context = null)
    {
    }

    public Task<bool> SendMessageAsync(
        SessionInfo session,
        MessageCommand command,
        bool useStickyMessages,
        string context,
        string detail,
        bool enableLogging,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        SentMessages.Add((session, command, useStickyMessages));
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
        : this(freeMiBReadings.Select(GpuMemoryQueryResult.FromFreeMiB).ToArray())
    {
    }

    public SequencedGpuMemoryProvider(params GpuMemoryQueryResult[] readings)
    {
        _readings = new Queue<GpuMemoryQueryResult>(readings);
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
/// Holds the first GPU query open so a second admission can be started while it is in flight.
/// </summary>
internal sealed class BlockingFirstGpuMemoryProvider : IGpuMemoryProvider
{
    private readonly GpuMemoryQueryResult _result;
    private readonly TaskCompletionSource _firstQueryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseFirstQuery = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _queryCount;

    public BlockingFirstGpuMemoryProvider(int freeMiB)
    {
        _result = GpuMemoryQueryResult.FromFreeMiB(freeMiB);
    }

    public int QueryCount => Volatile.Read(ref _queryCount);

    public Task FirstQueryStarted => _firstQueryStarted.Task;

    public void ReleaseFirstQuery() => _releaseFirstQuery.TrySetResult();

    public async Task<GpuMemoryQueryResult> GetFreeMemoryAsync(
        int gpuIndex,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var queryNumber = Interlocked.Increment(ref _queryCount);
        if (queryNumber == 1)
        {
            _firstQueryStarted.TrySetResult();
            await _releaseFirstQuery.Task.WaitAsync(cancellationToken);
        }

        return _result;
    }
}

internal sealed class ObservingGpuMemoryProvider : IGpuMemoryProvider, IGpuProcessMemoryProvider
{
    private readonly Queue<GpuMemoryQueryResult> _freeReadings;
    private readonly GpuProcessMemoryQueryResult _processReading;

    public ObservingGpuMemoryProvider(int processUsedMiB, params int[] freeMiBReadings)
    {
        _freeReadings = new Queue<GpuMemoryQueryResult>(
            freeMiBReadings.Select(GpuMemoryQueryResult.FromFreeMiB));
        _processReading = GpuProcessMemoryQueryResult.FromUsedMiB(processUsedMiB);
    }

    public Task<GpuMemoryQueryResult> GetFreeMemoryAsync(
        int gpuIndex,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        lock (_freeReadings)
        {
            return Task.FromResult(_freeReadings.Dequeue());
        }
    }

    public Task<GpuProcessMemoryQueryResult> GetUsedMemoryAsync(
        int processId,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(_processReading);
    }
}

internal sealed class DelayedObservingGpuMemoryProvider : IGpuMemoryProvider, IGpuProcessMemoryProvider
{
    private readonly Queue<GpuMemoryQueryResult> _freeReadings;
    private readonly TaskCompletionSource _processQueryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseProcessQuery = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DelayedObservingGpuMemoryProvider(params int[] freeMiBReadings)
    {
        _freeReadings = new Queue<GpuMemoryQueryResult>(
            freeMiBReadings.Select(GpuMemoryQueryResult.FromFreeMiB));
    }

    public Task ProcessQueryStarted => _processQueryStarted.Task;

    public void ReleaseProcessQuery() => _releaseProcessQuery.TrySetResult();

    public Task<GpuMemoryQueryResult> GetFreeMemoryAsync(
        int gpuIndex,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        lock (_freeReadings)
        {
            return Task.FromResult(_freeReadings.Dequeue());
        }
    }

    public async Task<GpuProcessMemoryQueryResult> GetUsedMemoryAsync(
        int processId,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        _processQueryStarted.TrySetResult();
        await _releaseProcessQuery.Task.WaitAsync(cancellationToken);
        return GpuProcessMemoryQueryResult.FromUsedMiB(323);
    }
}

/// <summary>
/// A logger that violates the usual ILogger contract by throwing, used to prove diagnostic
/// infrastructure cannot change a GPU admission decision.
/// </summary>
internal sealed class ThrowingLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => throw new InvalidOperationException("logger failed");
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

    public void CancelPendingMessages(SessionInfo session, string? context = null)
    {
    }

    public Task<bool> SendMessageAsync(
        SessionInfo session,
        MessageCommand command,
        bool useStickyMessages,
        string context,
        string detail,
        bool enableLogging,
        ILogger logger,
        CancellationToken cancellationToken)
        => throw new System.Net.WebSockets.WebSocketException("the remote party closed the connection");
}

internal sealed class ActiveSessionController : ISessionController
{
    public bool IsSessionActive => true;

    public bool SupportsMediaControl => true;

    public Task SendMessage<T>(
        SessionMessageType name,
        Guid messageId,
        T data,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
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
