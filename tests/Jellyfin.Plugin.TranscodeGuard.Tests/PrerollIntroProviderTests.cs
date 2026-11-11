using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using Jellyfin.Plugin.TranscodeGuard.Prerolls;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

/// <summary>
/// Answers only the interface members a test scripts, by name (or "name/argument count" for
/// overloads). Anything else fails, so a new dependency on Jellyfin shows up as a test failure
/// rather than as a silent default.
/// </summary>
public class ScriptedProxy : DispatchProxy
{
    internal Dictionary<string, Func<object?[], object?>> Handlers { get; } = new(StringComparer.Ordinal);

    internal static T Create<T>(out ScriptedProxy script)
        where T : class
    {
        var fake = DispatchProxy.Create<T, ScriptedProxy>();
        script = (ScriptedProxy)(object)fake;
        return fake;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        args ??= Array.Empty<object?>();
        var name = targetMethod!.Name;
        if (Handlers.TryGetValue(name + "/" + args.Length, out var handler) || Handlers.TryGetValue(name, out handler))
        {
            return handler(args);
        }

        throw new NotSupportedException(targetMethod.DeclaringType?.Name + "." + name);
    }
}

[Collection(PlaybackMonitorCollection.Name)]
public sealed class PrerollIntroProviderTests : IDisposable
{
    private readonly Plugin? _previousPlugin = Plugin.Instance;
    private readonly ILibraryManager _previousLibraryManager = BaseItem.LibraryManager;
    private readonly IRecordingsManager _previousRecordingsManager = Video.RecordingsManager;
    private readonly string _root = Directory.CreateTempSubdirectory("transcode-guard-preroll-provider-").FullName;
    private readonly string _folder;
    private readonly PluginConfiguration _config;
    private readonly Dictionary<Guid, BaseItem> _items = new();
    private readonly Dictionary<Guid, UserItemData> _userData = new();
    private readonly List<Guid> _created = new();
    private readonly List<(Guid Id, MetadataRefreshOptions Options)> _probes = new();
    private readonly ListLogger<PrerollIntroProvider> _logger = new();
    private readonly User _user = new("viewer", "auth-provider", "reset-provider");
    private readonly Movie _movie;
    private readonly PlaybackSessionManager _sessions;
    private readonly ILibraryManager _library;
    private readonly ScriptedProxy _libraryScript;
    private readonly IUserDataManager _userDataManager;
    private readonly ScriptedProxy _userDataScript;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IAuthorizationContext _authorization;
    private readonly PrerollIntroProvider _provider;
    private AuthorizationInfo _request = new() { Client = "Jellyfin Web", DeviceId = "device-1" };
    private Exception? _authorizationFailure;

    public PrerollIntroProviderTests()
    {
        _folder = Directory.CreateDirectory(Path.Join(_root, "prerolls")).FullName;
        File.WriteAllText(Path.Join(_folder, "02-rules.mp4"), string.Empty);
        File.WriteAllText(Path.Join(_folder, "01-welcome.mp4"), string.Empty);
        File.WriteAllText(Path.Join(_folder, "notes.txt"), string.Empty);
        _movie = new Movie { Id = Guid.NewGuid(), Name = "Movie", Path = Path.Join(_root, "movies", "movie.mkv") };

        var plugin = new Plugin(new TestApplicationPaths(Path.Join(_root, "plugin")), new PlaybackConfigurationSerializer());
        _config = plugin.Configuration;
        _config.EnablePrerolls = true;
        _config.PrerollFolder = _folder;
        _config.PrerollSessionLimit = 0;

        _library = ScriptedProxy.Create<ILibraryManager>(out _libraryScript);
        _libraryScript.Handlers["GetNewItemId"] = args => IdFor((string)args[0]!);
        _libraryScript.Handlers["GetItemById/1"] = args => _items.GetValueOrDefault((Guid)args[0]!);
        _libraryScript.Handlers["CreateItem"] = args =>
        {
            var item = (BaseItem)args[0]!;
            Assert.Null(args[1]);
            _items[item.Id] = item;
            _created.Add(item.Id);
            return null;
        };
        _libraryScript.Handlers["GetCollectionFolders/1"] = _ => new List<Folder>();

        // BaseItem's visibility checks go through this static rather than the injected manager.
        BaseItem.LibraryManager = _library;

        // So does Video.SourceType, which asks whether the file is a recording in progress.
        var recordings = ScriptedProxy.Create<IRecordingsManager>(out var recordingsScript);
        recordingsScript.Handlers["GetActiveRecordingInfo"] = _ => null;
        Video.RecordingsManager = recordings;

        _userDataManager = ScriptedProxy.Create<IUserDataManager>(out _userDataScript);
        _userDataScript.Handlers["GetUserData"] = args => _userData.GetValueOrDefault(((BaseItem)args[1]!).Id);

        _providerManager = ScriptedProxy.Create<IProviderManager>(out var providerScript);
        providerScript.Handlers["QueueRefresh"] = args =>
        {
            _probes.Add(((Guid)args[0]!, (MetadataRefreshOptions)args[1]!));
            return null;
        };

        _fileSystem = ScriptedProxy.Create<IFileSystem>(out _);
        _authorization = ScriptedProxy.Create<IAuthorizationContext>(out var authorizationScript);
        authorizationScript.Handlers["GetAuthorizationInfo"] = _ => _authorizationFailure != null
            ? Task.FromException<AuthorizationInfo>(_authorizationFailure)
            : Task.FromResult(_request);

        var sessionManager = DispatchProxy.Create<ISessionManager, PlaybackSessionManager>();
        _sessions = (PlaybackSessionManager)sessionManager;
        _provider = CreateProvider(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
    }

    public void Dispose()
    {
        _provider.Dispose();
        BaseItem.LibraryManager = _previousLibraryManager;
        Video.RecordingsManager = _previousRecordingsManager;
        typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, _previousPlugin);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task TheFirstFilePlaysAndEveryFileBecomesAHiddenProbedLibraryItem()
    {
        Assert.Equal("Transcode Guard Pre-rolls", _provider.Name);

        Assert.Equal(new[] { "01-welcome.mp4" }, await IntroFileNamesAsync());

        Assert.Equal(new[] { "01-welcome", "02-rules" }, _created.Select(id => _items[id].Name));
        Assert.All(_created, id => Assert.IsType<Video>(_items[id]));
        Assert.Equal(_created, _probes.Select(probe => probe.Id));
        Assert.All(_probes, probe =>
        {
            Assert.Equal(MetadataRefreshMode.FullRefresh, probe.Options.MetadataRefreshMode);
            Assert.Equal(MetadataRefreshMode.ValidationOnly, probe.Options.ImageRefreshMode);
        });
        Assert.Contains(_logger.Entries, entry => entry.Message == "Playing 1 pre-roll(s) for viewer on Jellyfin Web before Movie");

        // The next playback reuses the items rather than registering the files again.
        await IntroFileNamesAsync();
        Assert.Equal(2, _created.Count);
        Assert.Equal(2, _probes.Count);
    }

    [Fact]
    public async Task AFileReplacedUnderTheSameNameIsProbedAgain()
    {
        await IntroFileNamesAsync();
        var welcome = Path.Join(_folder, "01-welcome.mp4");
        File.SetLastWriteTimeUtc(welcome, File.GetLastWriteTimeUtc(welcome).AddHours(1));

        await IntroFileNamesAsync();

        Assert.Equal(3, _probes.Count);
        Assert.Equal(IdFor(welcome), _probes[^1].Id);
        Assert.Equal(File.GetLastWriteTimeUtc(welcome), _items[IdFor(welcome)].DateModified);
    }

    [Fact]
    public async Task OncePerUserSkipsWhatTheUserHasStartedAndPlayAllPlaysTheRest()
    {
        File.WriteAllText(Path.Join(_folder, "03-thanks.mp4"), string.Empty);
        _config.PrerollOncePerUser = true;
        _config.PrerollPlayAll = true;
        MarkStarted("01-welcome.mp4");

        Assert.Equal(new[] { "02-rules.mp4", "03-thanks.mp4" }, await IntroFileNamesAsync());
    }

    [Fact]
    public async Task OnceEverythingIsSeenNothingPlaysAndTheSessionIsNotCharged()
    {
        _config.PrerollOncePerUser = true;
        _config.PrerollSessionLimit = 1;
        MarkStarted("01-welcome.mp4", played: true);
        MarkStarted("02-rules.mp4");

        Assert.Empty(await IntroFileNamesAsync());
        Assert.Contains(_logger.Entries, entry => entry.Message == "Skipping pre-rolls: no pre-roll is left to show viewer");

        _userData.Clear();
        Assert.Single(await IntroFileNamesAsync());
    }

    [Fact]
    public async Task TheSessionLimitHoldsUntilTheSessionEnds()
    {
        _config.PrerollSessionLimit = 1;

        Assert.Single(await IntroFileNamesAsync());
        Assert.Empty(await IntroFileNamesAsync());
        Assert.Contains(_logger.Entries, entry => entry.Message == "Skipping pre-rolls: this session already had its 1 pre-roll(s)");

        // Another device of the same user is a different session.
        _request = new AuthorizationInfo { Client = "Jellyfin Web", DeviceId = "device-2" };
        Assert.Single(await IntroFileNamesAsync());
        _request = new AuthorizationInfo { Client = "Jellyfin Web", DeviceId = "device-1" };

        await _sessions.RaiseAsync(nameof(ISessionManager.SessionEnded), new SessionEventArgs { SessionInfo = null! });
        Assert.Empty(await IntroFileNamesAsync());

        await _sessions.RaiseAsync(nameof(ISessionManager.SessionEnded), new SessionEventArgs { SessionInfo = TestSessions.Create("s1", "device-1", _user.Id) });
        Assert.Single(await IntroFileNamesAsync());
    }

    [Fact]
    public async Task TwoRequestsFromOneSessionAtOnceGetPrerollsOnlyOnce()
    {
        _config.PrerollSessionLimit = 1;
        var concurrent = Array.Empty<string>();
        var entered = false;

        // The second request arrives while the first is still reading the user's play state,
        // after both have passed the early limit check.
        _userDataScript.Handlers["GetUserData"] = _ =>
        {
            if (!entered)
            {
                entered = true;
                concurrent = IntroFileNamesAsync().GetAwaiter().GetResult();
            }

            return null;
        };

        Assert.Empty(await IntroFileNamesAsync());
        Assert.Equal(new[] { "01-welcome.mp4" }, concurrent);
    }

    [Fact]
    public async Task NothingPlaysWhileTheFeatureIsOffOrThePluginIsNotLoaded()
    {
        _config.EnablePrerolls = false;
        Assert.Empty(await IntroFileNamesAsync());

        _config.EnablePrerolls = true;
        typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, null);
        Assert.Empty(await IntroFileNamesAsync());
        Assert.Empty(_created);
    }

    [Fact]
    public async Task AnyVideoGetsPrerollsButAudioAndPrerollsThemselvesDoNot()
    {
        Assert.Single(await IntroFileNamesAsync(new Video { Name = "Home video", Path = Path.Join(_root, "home.mp4") }));
        Assert.Empty(await IntroFileNamesAsync(new AudioItem { Name = "Song", Path = Path.Join(_root, "song.flac") }));
        Assert.Empty(await IntroFileNamesAsync(new Video { Name = "Pre-roll", Path = Path.Join(_folder, "01-welcome.mp4") }));
    }

    [Fact]
    public async Task ExcludedUsersGetNothing()
    {
        _config.PrerollExcludedUserIds = new[] { _user.Id.ToString("N") };

        Assert.Empty(await IntroFileNamesAsync());
        Assert.Contains(_logger.Entries, entry => entry.Message == "Skipping pre-rolls: user viewer is excluded");
    }

    [Fact]
    public async Task TheClientFiltersMatchTheAppThatAskedForIntros()
    {
        _config.PrerollExcludedClientPatterns = new[] { "web" };
        Assert.Empty(await IntroFileNamesAsync());
        Assert.Contains(_logger.Entries, entry => entry.Message == "Skipping pre-rolls: client Jellyfin Web is filtered out");

        _config.PrerollExcludedClientPatterns = Array.Empty<string>();
        _config.PrerollIncludedClientPatterns = new[] { "Android TV" };
        Assert.Empty(await IntroFileNamesAsync());

        _request = new AuthorizationInfo { Client = "Jellyfin Android TV", DeviceId = "tv" };
        Assert.Single(await IntroFileNamesAsync());
    }

    [Fact]
    public async Task ARequestWhoseClientCannotBeReadCountsAsAnUnknownClient()
    {
        _authorizationFailure = new InvalidOperationException("bad header");
        Assert.Single(await IntroFileNamesAsync());
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message == "Could not read the client of a pre-roll request");

        // An include list is a positive match, which an unknown client cannot make.
        _config.PrerollIncludedClientPatterns = new[] { "Jellyfin Web" };
        Assert.Empty(await IntroFileNamesAsync());
        Assert.Contains(_logger.Entries, entry => entry.Message == "Skipping pre-rolls: client Unknown is filtered out");
    }

    [Fact]
    public async Task WithoutAnHttpRequestTheClientIsUnknown()
    {
        using var provider = CreateProvider(null);
        _config.PrerollIncludedClientPatterns = new[] { "Jellyfin Web" };

        Assert.Empty(await provider.GetIntros(_movie, _user));

        _config.PrerollIncludedClientPatterns = Array.Empty<string>();
        Assert.Single(await provider.GetIntros(_movie, _user));

        using var noContext = CreateProvider(new HttpContextAccessor());
        Assert.Single(await noContext.GetIntros(_movie, _user));
    }

    [Fact]
    public async Task AZeroChanceNeverPlays()
    {
        _config.PrerollChancePercent = 0;

        Assert.Empty(await IntroFileNamesAsync());
        Assert.Contains(_logger.Entries, entry => entry.Message == "Skipping pre-rolls: the 0% chance did not come up");
    }

    [Fact]
    public async Task QuietWhenLoggingIsOff()
    {
        _config.EnableLogging = false;
        _config.PrerollChancePercent = 0;
        await IntroFileNamesAsync();
        _config.PrerollChancePercent = 100;
        await IntroFileNamesAsync();

        Assert.DoesNotContain(_logger.Entries, entry => entry.Message.Contains("pre-roll(s) for", StringComparison.Ordinal) || entry.Message.StartsWith("Skipping", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABrokenLibraryNeverStopsTheVideoFromPlaying()
    {
        _libraryScript.Handlers["GetNewItemId"] = _ => throw new InvalidOperationException("database is locked");

        Assert.Empty(await IntroFileNamesAsync());
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message == "Could not pick pre-rolls for Movie; playing it without them");
    }

    [Fact]
    public async Task DisposingStopsListeningForEndedSessions()
    {
        Assert.Equal(1, _sessions.SubscriberCount);

        _provider.Dispose();

        Assert.Equal(0, _sessions.SubscriberCount);
        await _sessions.RaiseAsync(nameof(ISessionManager.SessionEnded), new SessionEventArgs { SessionInfo = TestSessions.Create("s1", "device-1", _user.Id) });
    }

    private static Guid IdFor(string path)
    {
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(path)));
    }

    private PrerollIntroProvider CreateProvider(IHttpContextAccessor? httpContextAccessor)
    {
        return new PrerollIntroProvider(
            _library,
            _userDataManager,
            _providerManager,
            _fileSystem,
            (ISessionManager)(object)_sessions,
            _authorization,
            _logger,
            httpContextAccessor);
    }

    private void MarkStarted(string fileName, bool played = false)
    {
        _userData[IdFor(Path.Join(_folder, fileName))] = new UserItemData
        {
            Key = fileName,
            PlayCount = played ? 0 : 1,
            Played = played,
            LastPlayedDate = DateTime.UtcNow
        };
    }

    private async Task<string[]> IntroFileNamesAsync(BaseItem? item = null)
    {
        var intros = await _provider.GetIntros(item ?? _movie, _user);
        return intros.Select(intro => Path.GetFileName(_items[intro.ItemId!.Value].Path)).ToArray();
    }
}
