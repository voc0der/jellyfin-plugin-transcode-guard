using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeGuard.Prerolls;

/// <summary>
/// Registers server-selected videos as standalone Jellyfin items, shared by cinema intros
/// and the transcode limit's single-item replacement queue.
/// </summary>
public sealed class PrerollVideos(
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    IFileSystem fileSystem,
    ISessionManager sessionManager,
    ILogger<PrerollVideos> logger)
{
    private readonly object _registrationLock = new();

    internal async Task<bool> TryPlayLimitClipAsync(string path, SessionInfo session, CancellationToken cancellationToken)
    {
        if (!session.SupportsRemoteControl)
        {
            return false;
        }

        if (!Path.IsPathFullyQualified(path) || !PrerollRules.IsVideoFile(path) || !File.Exists(path))
        {
            logger.LogWarning("Transcode limit clip is missing or is not a video file: {Path}", path);
            return false;
        }

        var video = GetOrCreateItem(Path.GetFullPath(path));
        // Some clients retry the refused stream even after starting its replacement.
        if (session.NowPlayingItem?.Id == video.Id)
        {
            return true;
        }

        await sessionManager.SendPlayCommand(string.Empty, session.Id, new PlayRequest
        {
            PlayCommand = PlayCommand.PlayNow,
            ItemIds = [video.Id],
            StartPositionTicks = 0
        }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal Video GetOrCreateItem(string path)
    {
        // Two sessions can request a new clip before either has finished registering it.
        lock (_registrationLock)
        {
            return RegisterItem(path);
        }
    }

    private Video RegisterItem(string path)
    {
        var id = libraryManager.GetNewItemId(path, typeof(Video));
        var lastWrite = File.GetLastWriteTimeUtc(path);

        if (libraryManager.GetItemById(id) is Video existing)
        {
            // A file replaced under the same name needs probing again, or clients get the old
            // file's runtime and streams.
            if (Math.Abs((existing.DateModified - lastWrite).TotalSeconds) > 1)
            {
                existing.DateModified = lastWrite;
                QueueProbe(existing.Id);
            }

            return existing;
        }

        var video = new Video
        {
            Id = id,
            Path = path,
            Name = Path.GetFileNameWithoutExtension(path),
            DateCreated = DateTime.UtcNow,
            DateModified = lastWrite
        };

        libraryManager.CreateItem(video, null);

        // Probing in the background is enough: if a client asks to play the item before the
        // probe lands, Jellyfin probes it on the spot.
        QueueProbe(id);
        logger.LogInformation("Registered pre-roll {Path}", path);
        return video;
    }

    private void QueueProbe(Guid id)
    {
        providerManager.QueueRefresh(
            id,
            new MetadataRefreshOptions(new DirectoryService(fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,

                // Nothing shows a pre-roll's artwork, and grabbing a frame from a short clip
                // only fills the log with screen-grabber errors.
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly
            },
            RefreshPriority.High);
    }
}
