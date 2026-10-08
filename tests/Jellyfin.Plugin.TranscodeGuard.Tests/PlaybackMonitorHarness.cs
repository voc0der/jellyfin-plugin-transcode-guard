using System.Reflection;
using Jellyfin.Plugin.TranscodeGuard.Messaging;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

// These tests temporarily install Plugin.Instance, which other services also read.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlaybackMonitorCollection
{
    public const string Name = "Playback monitor plugin instance";
}

/// <summary>
/// Exposes Jellyfin's session events without implementing its unrelated playback APIs.
/// Unexpected calls fail instead of silently returning a default value.
/// </summary>
public class PlaybackSessionManager : DispatchProxy
{
    private readonly Dictionary<string, Delegate?> _handlers = new();

    internal List<SessionInfo> Sessions { get; } = new();

    internal int SubscriberCount => _handlers.Values.Sum(handler => handler?.GetInvocationList().Length ?? 0);

    internal Task RaiseAsync(string eventName, EventArgs args)
        => EventCompletion.RunAsync(() => _handlers.GetValueOrDefault(eventName)?.DynamicInvoke(this, args));

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        if (name == "get_Sessions")
        {
            return Sessions.ToArray();
        }

        if (name.StartsWith("add_", StringComparison.Ordinal))
        {
            var eventName = name[4..];
            _handlers[eventName] = Delegate.Combine(_handlers.GetValueOrDefault(eventName), (Delegate)args![0]!);
            return null;
        }

        if (name.StartsWith("remove_", StringComparison.Ordinal))
        {
            var eventName = name[7..];
            _handlers[eventName] = Delegate.Remove(_handlers.GetValueOrDefault(eventName), (Delegate)args![0]!);
            return null;
        }

        throw new NotSupportedException(name);
    }

    /// <summary>
    /// Async-void event handlers notify their original synchronization context when they finish.
    /// Await that notification so even assertions that no message was sent wait for the handler.
    /// </summary>
    private sealed class EventCompletion : SynchronizationContext
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _operations = 1;

        internal static Task RunAsync(Action action)
        {
            var context = new EventCompletion();
            var previous = Current;
            SetSynchronizationContext(context);
            try
            {
                action();
            }
            catch (Exception ex)
            {
                context._completed.TrySetException(ex);
            }
            finally
            {
                SetSynchronizationContext(previous);
                context.OperationCompleted();
            }

            return context._completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        public override void OperationStarted() => Interlocked.Increment(ref _operations);

        public override void OperationCompleted()
        {
            if (Interlocked.Decrement(ref _operations) == 0)
            {
                _completed.TrySetResult();
            }
        }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            OperationStarted();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    callback(state);
                }
                catch (Exception ex)
                {
                    _completed.TrySetException(ex);
                }
                finally
                {
                    SetSynchronizationContext(previous);
                    OperationCompleted();
                }
            });
        }
    }
}

internal sealed class PlaybackMessages : IClientMessageService
{
    internal sealed record Delivery(
        SessionInfo Session,
        MessageCommand Command,
        bool Sticky,
        string Context,
        IReadOnlyDictionary<string, string>? Arguments);

    internal List<Delivery> Deliveries { get; } = new();

    internal List<(SessionInfo Session, string? Context)> Cancellations { get; } = new();

    internal Func<Delivery, Task<bool>> Send { get; set; } = _ => Task.FromResult(true);

    public SessionInfo? ResolveSession(string? deviceId, Guid userId, Guid itemId)
        => throw new NotSupportedException();

    public void CancelPendingMessages(SessionInfo session, string? context = null)
        => Cancellations.Add((session, context));

    public Task<bool> SendMessageAsync(
        SessionInfo session, MessageCommand command, bool useStickyMessages, string context,
        string detail, bool enableLogging, ILogger logger, CancellationToken cancellationToken)
        => SendMessageAsync(session, command, null, useStickyMessages, context, detail, enableLogging, logger, cancellationToken);

    public Task<bool> SendMessageAsync(
        SessionInfo session, MessageCommand command, IReadOnlyDictionary<string, string>? extraArguments,
        bool useStickyMessages, string context, string detail, bool enableLogging,
        ILogger logger, CancellationToken cancellationToken)
    {
        var delivery = new Delivery(session, command, useStickyMessages, context, extraArguments);
        Deliveries.Add(delivery);
        return Send(delivery);
    }
}

internal sealed class PlaybackConfigurationSerializer : IXmlSerializer
{
    // BasePlugin saves its default configuration on first access. These tests keep that
    // configuration in memory; an unexpected attempt to load existing settings should fail.
    public void SerializeToFile(object obj, string file) { }

    public object DeserializeFromFile(Type type, string file) => throw new NotSupportedException();

    public object DeserializeFromStream(Type type, Stream stream) => throw new NotSupportedException();

    public object DeserializeFromBytes(Type type, byte[] buffer) => throw new NotSupportedException();

    public void SerializeToStream(object obj, Stream stream) => throw new NotSupportedException();
}
