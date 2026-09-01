using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using Jellyfin.Plugin.TranscodeGuard.Messaging;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeGuard.Sessions;

/// <summary>
/// Ends transcodes that have been sitting paused past the configured timeout, so their FFmpeg
/// process stops holding VRAM, an encoder session, and its output files for a viewer who is not
/// coming back.
/// </summary>
/// <remarks>
/// The clean path is a Stop playstate command: the client stops, reports playback stopped, and
/// Jellyfin tears the job down the same way it would for any other stop, leaving the resume point
/// the last progress report saved. Killing the FFmpeg job is only the backstop for clients that
/// ignore the command, and it ends the transcode alone - the session stays connected and the user
/// stays signed in.
/// </remarks>
public sealed class PausedTranscodeReaper : IHostedService, IDisposable
{
    private const string WarningMessageContext = "paused transcode warning";
    private const string DefaultWarningHeader = "Still there?";
    private const string DefaultWarningMessage = "Your paused video will be stopped in {{minutes}} minute(s) to free up server resources. Press play to keep watching.";

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    private readonly Func<IEnumerable<SessionInfo>> _sessionsAccessor;
    private readonly Func<SessionInfo, CancellationToken, Task> _stopSender;
    private readonly IClientMessageService _clientMessageService;
    private readonly IServiceProvider? _serviceProvider;
    private readonly ILogger<PausedTranscodeReaper> _logger;
    private readonly PausedTranscodeTracker _tracker = new();
    private readonly CancellationTokenSource _stopping = new();

    private Func<string, Task>? _transcodeKiller;
    private bool _transcodeKillerResolved;
    private Timer? _timer;
    private int _tickInProgress;
    private bool _disposed;

    public PausedTranscodeReaper(
        ISessionManager sessionManager,
        IClientMessageService clientMessageService,
        IServiceProvider serviceProvider,
        ILogger<PausedTranscodeReaper> logger)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);

        _sessionsAccessor = () => sessionManager.Sessions;
        _stopSender = (session, cancellationToken) => sessionManager.SendPlaystateCommand(
            null,
            session.Id,
            new PlaystateRequest { Command = PlaystateCommand.Stop },
            cancellationToken);
        _clientMessageService = clientMessageService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    internal PausedTranscodeReaper(
        Func<IEnumerable<SessionInfo>> sessionsAccessor,
        Func<SessionInfo, CancellationToken, Task> stopSender,
        Func<string, Task>? transcodeKiller,
        IClientMessageService clientMessageService,
        ILogger<PausedTranscodeReaper> logger)
    {
        _sessionsAccessor = sessionsAccessor ?? throw new ArgumentNullException(nameof(sessionsAccessor));
        _stopSender = stopSender ?? throw new ArgumentNullException(nameof(stopSender));
        _transcodeKiller = transcodeKiller;
        _transcodeKillerResolved = true;
        _clientMessageService = clientMessageService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(OnTick, null, TickInterval, TickInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    private void OnTick(object? state)
    {
        // A tick that outlives its interval must not overlap the next one: two passes over the
        // same session would send the stop command twice.
        if (Interlocked.CompareExchange(ref _tickInProgress, 1, 0) != 0)
        {
            return;
        }

        _ = RunTickAsync();
    }

    private async Task RunTickAsync()
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return;
            }

            await RunOnceAsync(config, DateTime.UtcNow).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error reaping paused transcodes");
        }
        finally
        {
            Interlocked.Exchange(ref _tickInProgress, 0);
        }
    }

    /// <summary>
    /// Runs one pass of the escalation. Separated from the timer so tests can drive it with their
    /// own clock.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="utcNow">The current UTC time.</param>
    /// <returns>A task that completes when every due action has been attempted.</returns>
    internal async Task RunOnceAsync(PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!config.EnablePausedTranscodeReaper)
        {
            // Nobody should inherit a part-run clock from the last time this was switched on.
            _tracker.Reset();
            return;
        }

        var verdicts = _tracker.Evaluate(_sessionsAccessor(), config, utcNow);

        foreach (var verdict in verdicts)
        {
            // One session's failure is not the next session's problem.
            try
            {
                await ApplyAsync(verdict, config).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(
                    ex,
                    "Failed to {Action} the paused transcode on session {SessionId}",
                    verdict.Action,
                    verdict.Session.Id ?? "Unknown");
            }
        }
    }

    private Task ApplyAsync(PausedTranscodeVerdict verdict, PluginConfiguration config)
        => verdict.Action switch
        {
            PausedTranscodeAction.Warn => SendWarningAsync(verdict, config),
            PausedTranscodeAction.Stop => SendStopAsync(verdict, config),
            PausedTranscodeAction.Kill => KillAsync(verdict),
            _ => Task.CompletedTask
        };

    private async Task SendWarningAsync(PausedTranscodeVerdict verdict, PluginConfiguration config)
    {
        var session = verdict.Session;

        await _clientMessageService.SendMessageAsync(
            session,
            new MessageCommand
            {
                // An admin who blanks these fields should still get a usable popup.
                Header = Fallback(config.PausedTranscodeWarningHeader, DefaultWarningHeader),
                Text = PausedTranscodeTracker.FormatWarningMessage(
                    Fallback(config.PausedTranscodeWarningMessage, DefaultWarningMessage),
                    verdict.MinutesUntilStop),
                TimeoutMs = config.MessageTimeoutMs
            },
            config.UseStickyPausedTranscodeMessages,
            WarningMessageContext,
            FormatPausedFor(verdict),
            config.EnableLogging,
            _logger,
            _stopping.Token).ConfigureAwait(false);
    }

    private async Task SendStopAsync(PausedTranscodeVerdict verdict, PluginConfiguration config)
    {
        var session = verdict.Session;

        _logger.LogInformation(
            "Stopping the transcode for {ItemName} on session {SessionId} ({UserName} / {Client}): paused for {PausedMinutes} minute(s), limit is {TimeoutMinutes}",
            session.NowPlayingItem?.Name ?? "Unknown",
            session.Id ?? "Unknown",
            session.UserName ?? "Unknown",
            session.Client ?? "Unknown",
            Math.Round(verdict.PausedFor.TotalMinutes),
            PausedTranscodeTracker.ResolveTimeoutMinutes(config));

        // The client is asked rather than told: stopping this way reports playback stopped, which
        // is what tears the FFmpeg job down cleanly and keeps the resume point tidy.
        await _stopSender(session, _stopping.Token).ConfigureAwait(false);
    }

    private async Task KillAsync(PausedTranscodeVerdict verdict)
    {
        var session = verdict.Session;
        var deviceId = session.DeviceId;

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            _logger.LogWarning(
                "Session {SessionId} ignored the stop command and has no device ID, so its transcode cannot be ended",
                session.Id ?? "Unknown");
            return;
        }

        var killer = ResolveTranscodeKiller();
        if (killer == null)
        {
            return;
        }

        _logger.LogInformation(
            "Session {SessionId} ({UserName} / {Client}) did not act on the stop command; ending its FFmpeg job. The session stays signed in.",
            session.Id ?? "Unknown",
            session.UserName ?? "Unknown",
            session.Client ?? "Unknown");

        await killer(deviceId).ConfigureAwait(false);
    }

    private Func<string, Task>? ResolveTranscodeKiller()
    {
        if (_transcodeKillerResolved)
        {
            return _transcodeKiller;
        }

        _transcodeKillerResolved = true;

        try
        {
            _transcodeKiller = BuildTranscodeKiller();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(
                ex,
                "Jellyfin's transcode manager is unavailable, so a client that ignores the stop command keeps its transcode");
        }

        if (_transcodeKiller == null)
        {
            _logger.LogWarning(
                "Jellyfin's transcode manager could not be resolved; paused transcodes can only be stopped through the client, not ended server-side");
        }

        return _transcodeKiller;
    }

    /// <summary>
    /// Builds the kill callback over strings only, so nothing outside this method mentions
    /// <c>ITranscodeManager</c>.
    /// </summary>
    /// <remarks>
    /// That type does not exist before Jellyfin 10.10. Isolating it behind a non-inlined method
    /// keeps a type load failure on an older server inside the catch above, where it costs the
    /// server-side kill and nothing else, rather than taking the whole hosted service down.
    /// </remarks>
    /// <returns>A callback that ends every transcode belonging to a device, or null.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Func<string, Task>? BuildTranscodeKiller()
    {
        var transcodeManager = _serviceProvider?.GetService<ITranscodeManager>();
        if (transcodeManager == null)
        {
            return null;
        }

        // A null play session ID means "every job on this device", which is the only handle a
        // SessionInfo gives us. In practice a device has one transcode at a time, and this is the
        // same call Jellyfin makes for itself when playback stops.
        return deviceId => transcodeManager.KillTranscodingJobs(deviceId, null, _ => true);
    }

    private static string FormatPausedFor(PausedTranscodeVerdict verdict)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"Paused for {Math.Round(verdict.PausedFor.TotalMinutes)} minute(s)");

    private static string Fallback(string? configured, string defaultText)
        => string.IsNullOrWhiteSpace(configured) ? defaultText : configured;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _timer = null;

        // Pending warning deliveries are cancelled rather than left running against a stopped server.
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
