using Jellyfin.Plugin.TranscodeNag.Configuration;
using Jellyfin.Plugin.TranscodeNag.Gpu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.TranscodeNag.Tests;

public class GpuResourceGuardTests
{
    private const string CudaNvencArguments =
        "-init_hw_device cuda=cu:0 -filter_hw_device cu -hwaccel cuda -hwaccel_output_format cuda " +
        "-i \"/media/movie.mkv\" -codec:v:0 av1_nvenc -codec:a:0 libfdk_aac";

    private static readonly Guid AliceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MovieTwoId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static PluginConfiguration EnabledConfig(int thresholdMiB = 1500) => new()
    {
        EnableGpuResourceGuard = true,
        MinimumFreeGpuMemoryMiB = thresholdMiB,
        GpuIndex = 0,
        GpuCheckTimeoutMilliseconds = 1000
    };

    private static GpuResourceGuard CreateGuard(
        PluginConfiguration config,
        FakeGpuMemoryProvider provider,
        RecordingClientMessageService messages)
    {
        return new GpuResourceGuard(
            provider,
            messages,
            NullLogger<GpuResourceGuard>.Instance,
            () => config);
    }

    private static GpuTranscodeRequest HardwareTranscodeRequest(
        string deviceId = "device-2",
        string playSessionId = "play-2")
    {
        return new GpuTranscodeRequest
        {
            IsVideoRequest = true,
            OutputVideoCodec = "av1",
            CommandLineArguments = CudaNvencArguments,
            DeviceId = deviceId,
            PlaySessionId = playSessionId,
            UserId = AliceId,
            ItemId = MovieTwoId,
            ItemName = "Movie Two"
        };
    }

    [Fact]
    public async Task IsAdmittedAsync_GuardDisabled_AllowsWithoutQueryingTheGpu()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(10);
        var messages = new RecordingClientMessageService();
        var guard = CreateGuard(new PluginConfiguration { EnableGpuResourceGuard = false }, provider, messages);

        Assert.True(await guard.IsAdmittedAsync(HardwareTranscodeRequest(), CancellationToken.None));
        Assert.Equal(0, provider.QueryCount);
        Assert.Empty(messages.SentMessages);
    }

    [Fact]
    public async Task IsAdmittedAsync_RemuxIsAllowedWithoutQueryingTheGpu()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(10);
        var messages = new RecordingClientMessageService();
        var guard = CreateGuard(EnabledConfig(), provider, messages);

        var request = HardwareTranscodeRequest();
        request.OutputVideoCodec = "copy";

        Assert.True(await guard.IsAdmittedAsync(request, CancellationToken.None));
        Assert.Equal(0, provider.QueryCount);
        Assert.Empty(messages.SentMessages);
    }

    [Fact]
    public async Task IsAdmittedAsync_AudioOnlyIsAllowedWithoutQueryingTheGpu()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(10);
        var messages = new RecordingClientMessageService();
        var guard = CreateGuard(EnabledConfig(), provider, messages);

        var request = HardwareTranscodeRequest();
        request.IsVideoRequest = false;
        request.OutputVideoCodec = null;
        request.CommandLineArguments = "-i \"/media/song.flac\" -codec:a:0 libmp3lame out.mp3";

        Assert.True(await guard.IsAdmittedAsync(request, CancellationToken.None));
        Assert.Equal(0, provider.QueryCount);
    }

    [Fact]
    public async Task IsAdmittedAsync_SufficientVramAllowsAndSendsNothing()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(2500);
        var messages = new RecordingClientMessageService();
        messages.AddSession(TestSessions.Create("session-2", "device-2", AliceId));
        var guard = CreateGuard(EnabledConfig(1500), provider, messages);

        Assert.True(await guard.IsAdmittedAsync(HardwareTranscodeRequest(), CancellationToken.None));
        Assert.Equal(1, provider.QueryCount);
        Assert.Equal(0, provider.LastGpuIndex);
        Assert.Empty(messages.SentMessages);
    }

    [Fact]
    public async Task IsAdmittedAsync_InsufficientVramDeniesAndMessagesTheRequestingClientOnce()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(700);
        var messages = new RecordingClientMessageService();
        var session = TestSessions.Create("session-2", "device-2", AliceId);
        messages.AddSession(session);

        var config = EnabledConfig(1500);
        var guard = CreateGuard(config, provider, messages);

        Assert.False(await guard.IsAdmittedAsync(HardwareTranscodeRequest(), CancellationToken.None));

        var sent = Assert.Single(messages.SentMessages);
        Assert.Same(session, sent.Session);
        Assert.Equal(config.GpuGuardDeniedHeader, sent.Command.Header);
        Assert.Equal(config.GpuGuardDeniedMessage, sent.Command.Text);
    }

    [Fact]
    public async Task DenialMessage_LeaksNoServerSideDetail()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(700);
        var messages = new RecordingClientMessageService();
        messages.AddSession(TestSessions.Create("session-2", "device-2", AliceId));
        var guard = CreateGuard(EnabledConfig(1500), provider, messages);

        await guard.IsAdmittedAsync(HardwareTranscodeRequest(), CancellationToken.None);

        var text = Assert.Single(messages.SentMessages).Command.Text + Assert.Single(messages.SentMessages).Command.Header;

        foreach (var forbidden in new[] { "700", "1500", "nvenc", "cuda", "VRAM", "187", "nvidia-smi" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task IsAdmittedAsync_RepeatedDenialsOfTheSameStreamSendOneNotification()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(700);
        var messages = new RecordingClientMessageService();
        messages.AddSession(TestSessions.Create("session-2", "device-2", AliceId));
        var guard = CreateGuard(EnabledConfig(1500), provider, messages);

        // Jellyfin retries the same segment several times within a second or two.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.False(await guard.IsAdmittedAsync(HardwareTranscodeRequest(), CancellationToken.None));
        }

        // Every attempt is still refused; only the client popup is de-duplicated.
        Assert.Single(messages.SentMessages);
    }

    [Fact]
    public async Task IsAdmittedAsync_ParallelSessionsEachReceiveOnlyTheirOwnDenial()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(700);
        var messages = new RecordingClientMessageService();
        var sessionOne = TestSessions.Create("session-1", "device-1", AliceId, "alice");
        var sessionTwo = TestSessions.Create("session-2", "device-2", AliceId, "alice");
        messages.AddSession(sessionOne);
        messages.AddSession(sessionTwo);

        var guard = CreateGuard(EnabledConfig(1500), provider, messages);

        await guard.IsAdmittedAsync(HardwareTranscodeRequest("device-1", "play-1"), CancellationToken.None);
        await guard.IsAdmittedAsync(HardwareTranscodeRequest("device-2", "play-2"), CancellationToken.None);

        Assert.Equal(2, messages.SentMessages.Count);
        Assert.Same(sessionOne, messages.SentMessages[0].Session);
        Assert.Same(sessionTwo, messages.SentMessages[1].Session);
    }

    [Fact]
    public async Task IsAdmittedAsync_QueryFailureFailsOpen()
    {
        var provider = FakeGpuMemoryProvider.Failing("nvidia-smi is not available");
        var messages = new RecordingClientMessageService();
        messages.AddSession(TestSessions.Create("session-2", "device-2", AliceId));
        var guard = CreateGuard(EnabledConfig(1500), provider, messages);

        Assert.True(await guard.IsAdmittedAsync(HardwareTranscodeRequest(), CancellationToken.None));
        Assert.Empty(messages.SentMessages);
    }

    [Fact]
    public async Task IsAdmittedAsync_AbsentGpuIndexFailsOpen()
    {
        var provider = FakeGpuMemoryProvider.Failing("nvidia-smi did not report a usable value for GPU 3");
        var messages = new RecordingClientMessageService();
        var config = EnabledConfig(1500);
        config.GpuIndex = 3;
        var guard = CreateGuard(config, provider, messages);

        Assert.True(await guard.IsAdmittedAsync(HardwareTranscodeRequest(), CancellationToken.None));
        Assert.Equal(3, provider.LastGpuIndex);
        Assert.Empty(messages.SentMessages);
    }

    [Fact]
    public async Task IsAdmittedAsync_DeniesEvenWhenNoSessionCanBeCorrelated()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(700);
        var messages = new RecordingClientMessageService();
        var guard = CreateGuard(EnabledConfig(1500), provider, messages);

        // No session registered for this device: the popup cannot be delivered, the refusal stands.
        Assert.False(await guard.IsAdmittedAsync(HardwareTranscodeRequest(), CancellationToken.None));
        Assert.Empty(messages.SentMessages);
    }

    [Fact]
    public async Task DenialMessage_FallsBackToDefaultsWhenTheAdminBlanksTheText()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(700);
        var messages = new RecordingClientMessageService();
        messages.AddSession(TestSessions.Create("session-2", "device-2", AliceId));

        var config = EnabledConfig(1500);
        config.GpuGuardDeniedHeader = string.Empty;
        config.GpuGuardDeniedMessage = "   ";

        var guard = CreateGuard(config, provider, messages);

        Assert.False(await guard.IsAdmittedAsync(HardwareTranscodeRequest(), CancellationToken.None));

        var sent = Assert.Single(messages.SentMessages);
        Assert.False(string.IsNullOrWhiteSpace(sent.Command.Header));
        Assert.False(string.IsNullOrWhiteSpace(sent.Command.Text));
    }

    [Fact]
    public async Task IsAdmittedAsync_DeniesAgainOnceTheSuppressionWindowHasNoEntry()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(700);
        var messages = new RecordingClientMessageService();
        messages.AddSession(TestSessions.Create("session-2", "device-2", AliceId));
        var guard = CreateGuard(EnabledConfig(1500), provider, messages);

        // A different play session is a different playback attempt, so it gets its own popup.
        await guard.IsAdmittedAsync(HardwareTranscodeRequest("device-2", "play-2"), CancellationToken.None);
        await guard.IsAdmittedAsync(HardwareTranscodeRequest("device-2", "play-3"), CancellationToken.None);

        Assert.Equal(2, messages.SentMessages.Count);
    }

    [Fact]
    public async Task IsAdmittedAsync_AllowsWhenConfigurationIsUnavailable()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(0);
        var messages = new RecordingClientMessageService();
        var guard = new GpuResourceGuard(
            provider,
            messages,
            NullLogger<GpuResourceGuard>.Instance,
            () => null);

        Assert.True(await guard.IsAdmittedAsync(HardwareTranscodeRequest(), CancellationToken.None));
        Assert.Equal(0, provider.QueryCount);
    }
}
