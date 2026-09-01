using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.TranscodeGuard.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeGuard.Data;

public class TranscodeEventStore
{
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<TranscodeEventStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private const string DataFileName = "events.json";

    public TranscodeEventStore(IApplicationPaths applicationPaths, ILogger<TranscodeEventStore> logger)
    {
        _appPaths = applicationPaths;
        _logger = logger;
    }

    private string GetDataFilePath()
    {
        var dataDir = Path.GetFullPath(Path.Join(_appPaths.DataPath, "plugins", "data", "TranscodeGuard"));
        Directory.CreateDirectory(dataDir);

        // Keep file segment normalized so future refactors cannot inject rooted paths here.
        var dataFileName = Path.GetFileName(DataFileName);
        return Path.Join(dataDir, dataFileName);
    }

    public async void AddEvent(TranscodeEvent transcodeEvent)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var events = await LoadEventsAsync().ConfigureAwait(false);

            // Maintain invariant: at most 1 improvement credit per user, and it is removed on the next bad transcode.
            ApplyInMemoryRules(events, transcodeEvent);

            events.Add(transcodeEvent);

            // Clean up old events (keep last 30 days)
            var cutoff = DateTime.UtcNow.AddDays(-30);
            events.RemoveAll(e => e.Timestamp < cutoff);

            await SaveEventsAsync(events).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Error adding transcode event");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Error adding transcode event");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Error adding transcode event");
        }
        catch (NotSupportedException ex)
        {
            _logger.LogError(ex, "Error adding transcode event");
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Error adding transcode event");
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogError(ex, "Error adding transcode event");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Error adding transcode event");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Get stats used for login/open nags.
    /// </summary>
    public async System.Threading.Tasks.Task<UserNagStatus> GetUserNagStatusAsync(string userId, int days, Func<TranscodeEvent, bool>? eventFilter = null)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var events = await LoadEventsAsync().ConfigureAwait(false);
            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-days);

            var userEvents = events
                .Where(e => e.UserId == userId && (eventFilter == null || eventFilter(e)))
                .ToList();
            var recentUserEvents = userEvents.Where(e => e.Timestamp >= cutoff).ToList();

            var badCount = recentUserEvents.Count(e => e.Kind == NagEventKind.BadTranscode);

            var lastBad = userEvents
                .Where(e => e.Kind == NagEventKind.BadTranscode)
                .OrderByDescending(e => e.Timestamp)
                .Select(e => (DateTime?)e.Timestamp)
                .FirstOrDefault();

            var hasCredit = false;
            if (lastBad.HasValue)
            {
                hasCredit = userEvents.Any(e => e.Kind == NagEventKind.ImprovementCredit && e.Timestamp > lastBad.Value);
            }

            var lastNag = userEvents
                .Where(e => e.Kind == NagEventKind.NagSent)
                .OrderByDescending(e => e.Timestamp)
                .Select(e => (DateTime?)e.Timestamp)
                .FirstOrDefault();

            var naggedRecently = lastNag.HasValue && lastNag.Value >= cutoff;

            return new UserNagStatus
            {
                UserId = userId,
                BadTranscodeCount = badCount,
                HasImprovementCredit = hasCredit,
                NaggedRecently = naggedRecently,
                LastBadTranscodeUtc = lastBad,
                LastNagUtc = lastNag
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    private void ApplyInMemoryRules(List<TranscodeEvent> events, TranscodeEvent incoming)
    {
        // If a user gets an improvement credit, keep only one (the latest).
        if (incoming.Kind == NagEventKind.ImprovementCredit)
        {
            events.RemoveAll(e => e.UserId == incoming.UserId && e.Kind == NagEventKind.ImprovementCredit);
            return;
        }

        // If a user bad-transcodes again, remove any previously recorded improvement credit.
        if (incoming.Kind == NagEventKind.BadTranscode)
        {
            events.RemoveAll(e => e.UserId == incoming.UserId && e.Kind == NagEventKind.ImprovementCredit);
        }
    }

    public async System.Threading.Tasks.Task<List<TranscodeEvent>> GetUserEventsAsync(string userId, int days, Func<TranscodeEvent, bool>? eventFilter = null)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var events = await LoadEventsAsync().ConfigureAwait(false);
            var cutoff = DateTime.UtcNow.AddDays(-days);

            return events
                .Where(e => e.UserId == userId && e.Timestamp >= cutoff && (eventFilter == null || eventFilter(e)))
                .OrderByDescending(e => e.Timestamp)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async System.Threading.Tasks.Task<List<TranscodeEvent>> LoadEventsAsync()
    {
        var filePath = GetDataFilePath();

        if (!File.Exists(filePath))
        {
            return new List<TranscodeEvent>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<TranscodeEvent>>(json) ?? new List<TranscodeEvent>();
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Error loading transcode events from {FilePath}", filePath);
            return new List<TranscodeEvent>();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Error loading transcode events from {FilePath}", filePath);
            return new List<TranscodeEvent>();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Error loading transcode events from {FilePath}", filePath);
            return new List<TranscodeEvent>();
        }
        catch (NotSupportedException ex)
        {
            _logger.LogError(ex, "Error loading transcode events from {FilePath}", filePath);
            return new List<TranscodeEvent>();
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Error loading transcode events from {FilePath}", filePath);
            return new List<TranscodeEvent>();
        }
    }

    private async System.Threading.Tasks.Task SaveEventsAsync(List<TranscodeEvent> events)
    {
        var filePath = GetDataFilePath();

        try
        {
            var json = JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Error saving transcode events to {FilePath}", filePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Error saving transcode events to {FilePath}", filePath);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Error saving transcode events to {FilePath}", filePath);
        }
        catch (NotSupportedException ex)
        {
            _logger.LogError(ex, "Error saving transcode events to {FilePath}", filePath);
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Error saving transcode events to {FilePath}", filePath);
        }
    }
}
