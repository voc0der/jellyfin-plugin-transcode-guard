using Jellyfin.Plugin.TranscodeNag.Configuration;
using Jellyfin.Plugin.TranscodeNag.Gpu;
using Jellyfin.Plugin.TranscodeNag.Messaging;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.TranscodeNag.Tests;

public class GuardedTranscodeManagerTests
{
    private const string CudaNvencArguments =
        "-init_hw_device cuda=cu:0 -filter_hw_device cu -hwaccel cuda -hwaccel_output_format cuda " +
        "-i \"/media/movie.mkv\" -codec:v:0 av1_nvenc -codec:a:0 libfdk_aac";

    private static readonly Guid AliceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MovieTwoId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>
    /// Stands in for Jellyfin's TranscodeManager: records whether FFmpeg would have been launched.
    /// </summary>
    private sealed class SpyTranscodeManager : ITranscodeManager, IDisposable
    {
        public int StartFfMpegCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public bool Disposed => DisposeCallCount > 0;

        public Task<TranscodingJob> StartFfMpeg(
            StreamState state,
            string outputPath,
            string commandLineArguments,
            Guid userId,
            TranscodingJobType transcodingJobType,
            CancellationTokenSource cancellationTokenSource,
            string? workingDirectory = null)
        {
            StartFfMpegCallCount++;
            return Task.FromResult(new TranscodingJob(NullLogger<TranscodingJob>.Instance));
        }

        public TranscodingJob? GetTranscodingJob(string playSessionId) => null;

        public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type) => null;

        public void PingTranscodingJob(string playSessionId, bool? isUserPaused)
        {
        }

        public Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles)
            => Task.CompletedTask;

        public void ReportTranscodingProgress(
            TranscodingJob job,
            StreamState state,
            TimeSpan? transcodingPosition,
            float? framerate,
            double? percentComplete,
            long? bytesTranscoded,
            int? bitRate)
        {
        }

        public TranscodingJob? OnTranscodeBeginRequest(string path, TranscodingJobType type) => null;

        public void OnTranscodeEndRequest(TranscodingJob job)
        {
        }

        public ValueTask<IDisposable> LockAsync(string outputPath, CancellationToken cancellationToken)
            => ValueTask.FromResult<IDisposable>(new MemoryStream());

        public void Dispose() => DisposeCallCount++;
    }

    private sealed class ThrowingGpuMemoryProvider : IGpuMemoryProvider
    {
        public Task<GpuMemoryQueryResult> GetFreeMemoryAsync(
            int gpuIndex,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    private static StreamState CreateHardwareVideoState(string outputVideoCodec = "av1")
    {
        var state = new StreamState(null!, TranscodingJobType.Hls, null!)
        {
            Request = new VideoRequestDto
            {
                Id = MovieTwoId,
                DeviceId = "device-2",
                PlaySessionId = "play-2"
            },
            OutputVideoCodec = outputVideoCodec,
            MediaSource = new MediaSourceInfo { Name = "Movie Two" }
        };

        return state;
    }

    private static (GuardedTranscodeManager Decorator, SpyTranscodeManager Inner, RecordingClientMessageService Messages)
        CreateDecorator(int freeMiB, int thresholdMiB, bool guardEnabled = true)
    {
        var config = new PluginConfiguration
        {
            EnableGpuResourceGuard = guardEnabled,
            MinimumFreeGpuMemoryMiB = thresholdMiB,
            GpuIndex = 0
        };

        var messages = new RecordingClientMessageService();
        messages.AddSession(TestSessions.Create("session-2", "device-2", AliceId));

        var guard = new GpuResourceGuard(
            FakeGpuMemoryProvider.WithFreeMiB(freeMiB),
            messages,
            NullLogger<GpuResourceGuard>.Instance,
            () => config);

        var inner = new SpyTranscodeManager();
        var decorator = new GuardedTranscodeManager(inner, guard, NullLogger<GuardedTranscodeManager>.Instance);

        return (decorator, inner, messages);
    }

    [Fact]
    public async Task StartFfMpeg_LowVramRefusesWithoutEverLaunchingFfmpeg()
    {
        var (decorator, inner, messages) = CreateDecorator(freeMiB: 700, thresholdMiB: 1500);
        using var cts = new CancellationTokenSource();

        // Jellyfin's own SecurityException, not the BCL type of the same name: its exception
        // middleware resolves the name against MediaBrowser.Controller.Net, maps this to HTTP 403,
        // and logs it without a stack trace. Verified against a live 10.11.11 server - the BCL
        // type falls through to 500 with a full trace.
        var ex = await Assert.ThrowsAsync<SecurityException>(() => decorator.StartFfMpeg(
            CreateHardwareVideoState(),
            "/config/transcodes/abc.m3u8",
            CudaNvencArguments,
            AliceId,
            TranscodingJobType.Hls,
            cts));

        Assert.Equal("MediaBrowser.Controller.Net.SecurityException", ex.GetType().FullName);
        Assert.Contains("free GPU memory", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, inner.StartFfMpegCallCount);
        Assert.Single(messages.SentMessages);
    }

    [Fact]
    public async Task StartFfMpeg_SufficientVramLaunchesNormally()
    {
        var (decorator, inner, messages) = CreateDecorator(freeMiB: 2500, thresholdMiB: 1500);
        using var cts = new CancellationTokenSource();

        var job = await decorator.StartFfMpeg(
            CreateHardwareVideoState(),
            "/config/transcodes/abc.m3u8",
            CudaNvencArguments,
            AliceId,
            TranscodingJobType.Hls,
            cts);

        Assert.NotNull(job);
        Assert.Equal(1, inner.StartFfMpegCallCount);
        Assert.Empty(messages.SentMessages);
    }

    [Fact]
    public async Task StartFfMpeg_RemuxLaunchesEvenWhenVramIsExhausted()
    {
        var (decorator, inner, messages) = CreateDecorator(freeMiB: 10, thresholdMiB: 1500);
        using var cts = new CancellationTokenSource();

        await decorator.StartFfMpeg(
            CreateHardwareVideoState("copy"),
            "/config/transcodes/abc.m3u8",
            "-i \"/media/movie.mkv\" -codec:v:0 copy -codec:a:0 libfdk_aac",
            AliceId,
            TranscodingJobType.Hls,
            cts);

        Assert.Equal(1, inner.StartFfMpegCallCount);
        Assert.Empty(messages.SentMessages);
    }

    [Fact]
    public async Task StartFfMpeg_GuardDisabledLeavesBehaviourUnchanged()
    {
        var (decorator, inner, _) = CreateDecorator(freeMiB: 10, thresholdMiB: 1500, guardEnabled: false);
        using var cts = new CancellationTokenSource();

        await decorator.StartFfMpeg(
            CreateHardwareVideoState(),
            "/config/transcodes/abc.m3u8",
            CudaNvencArguments,
            AliceId,
            TranscodingJobType.Hls,
            cts);

        Assert.Equal(1, inner.StartFfMpegCallCount);
    }

    [Fact]
    public async Task StartFfMpeg_AllowsPlaybackWhenTheGuardItselfFails()
    {
        // A guard that throws must never become an outage.
        var guard = new GpuResourceGuard(
            new ThrowingGpuMemoryProvider(),
            new RecordingClientMessageService(),
            NullLogger<GpuResourceGuard>.Instance,
            () => new PluginConfiguration { EnableGpuResourceGuard = true, MinimumFreeGpuMemoryMiB = 1500 });

        var inner = new SpyTranscodeManager();
        var decorator = new GuardedTranscodeManager(inner, guard, NullLogger<GuardedTranscodeManager>.Instance);
        using var cts = new CancellationTokenSource();

        await decorator.StartFfMpeg(
            CreateHardwareVideoState(),
            "/config/transcodes/abc.m3u8",
            CudaNvencArguments,
            AliceId,
            TranscodingJobType.Hls,
            cts);

        Assert.Equal(1, inner.StartFfMpegCallCount);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var (decorator, inner, _) = CreateDecorator(freeMiB: 2500, thresholdMiB: 1500);

        decorator.Dispose();
        decorator.Dispose();

        Assert.Equal(1, inner.DisposeCallCount);
    }

    [Fact]
    public void Dispose_DisposesTheInnerManager()
    {
        var (decorator, inner, _) = CreateDecorator(freeMiB: 2500, thresholdMiB: 1500);

        decorator.Dispose();

        Assert.True(inner.Disposed);
    }

    private static ServiceCollection BuildDecoratableCollection()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITranscodeManager, SpyTranscodeManager>();
        services.AddSingleton<IGpuMemoryProvider>(FakeGpuMemoryProvider.WithFreeMiB(2500));
        services.AddSingleton<IClientMessageService>(new RecordingClientMessageService());
        services.AddSingleton(new GpuResourceGuard(
            FakeGpuMemoryProvider.WithFreeMiB(2500),
            new RecordingClientMessageService(),
            NullLogger<GpuResourceGuard>.Instance,
            () => new PluginConfiguration()));
        services.AddSingleton<ILogger<GuardedTranscodeManager>>(NullLogger<GuardedTranscodeManager>.Instance);
        return services;
    }

    [Fact]
    public void DecorateTranscodeManager_ReplacesJellyfinsRegistrationInPlace()
    {
        var services = BuildDecoratableCollection();

        PluginServiceRegistrator.TryDecorateTranscodeManager(services);

        Assert.Null(PluginServiceRegistrator.DecorationFailure);

        // Exactly one registration must remain, or Jellyfin would resolve an unguarded manager.
        Assert.Single(services.Where(service => service.ServiceType == typeof(ITranscodeManager)));

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<ITranscodeManager>();

        var decorator = Assert.IsType<GuardedTranscodeManager>(resolved);
        Assert.Same(decorator, provider.GetRequiredService<ITranscodeManager>());
    }

    [Fact]
    public void DecorateTranscodeManager_LeavesAFactoryRegistrationAlone()
    {
        // Only a concrete implementation type can be rebuilt safely; anything else is left as-is.
        var services = new ServiceCollection();
        services.AddSingleton<ITranscodeManager>(_ => new SpyTranscodeManager());

        PluginServiceRegistrator.TryDecorateTranscodeManager(services);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<SpyTranscodeManager>(provider.GetRequiredService<ITranscodeManager>());
        Assert.NotNull(PluginServiceRegistrator.DecorationFailure);
    }

    [Fact]
    public void DecorateTranscodeManager_IsANoOpWhenNothingRegisteredTheService()
    {
        var services = new ServiceCollection();

        PluginServiceRegistrator.TryDecorateTranscodeManager(services);

        Assert.Empty(services);
        Assert.NotNull(PluginServiceRegistrator.DecorationFailure);
    }

    [Fact]
    public void DecorateTranscodeManager_RunTwiceDoesNotDoubleWrap()
    {
        // Guards against a reload path registering the plugin's services more than once.
        var services = BuildDecoratableCollection();

        PluginServiceRegistrator.TryDecorateTranscodeManager(services);
        PluginServiceRegistrator.TryDecorateTranscodeManager(services);

        Assert.Single(services.Where(service => service.ServiceType == typeof(ITranscodeManager)));

        using var provider = services.BuildServiceProvider();
        var decorator = Assert.IsType<GuardedTranscodeManager>(provider.GetRequiredService<ITranscodeManager>());
        Assert.NotNull(decorator);
    }
}
