using Jellyfin.Plugin.TranscodeNag.Configuration;
using Jellyfin.Plugin.TranscodeNag.Gpu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.TranscodeNag.Tests;

public class GpuResourceGuardTests
{
    private const string CudaNvencArguments =
        "-init_hw_device cuda=cu:0 -filter_hw_device cu -hwaccel cuda -hwaccel_output_format cuda " +
        "-i \"/media/movie.mkv\" -vf \"tonemap_cuda=format=yuv420p,scale_cuda=1920:1080\" " +
        "-codec:v:0 h264_nvenc -codec:a:0 libfdk_aac";

    private static readonly Guid AliceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MovieTwoId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static PluginConfiguration EnabledConfig() => new()
    {
        EnableGpuResourceGuard = true,
        GpuIndex = 0,
        GpuCheckTimeoutMilliseconds = 1000
    };

    private static GpuResourceGuard CreateGuard(
        PluginConfiguration config,
        IGpuMemoryProvider provider,
        RecordingClientMessageService messages)
    {
        return new GpuResourceGuard(
            provider,
            messages,
            NullLogger<GpuResourceGuard>.Instance,
            () => config);
    }

    private static GpuResourceGuard CreateGuard(
        PluginConfiguration config,
        IGpuMemoryProvider provider,
        RecordingClientMessageService messages,
        Func<DateTimeOffset> clock,
        TimeSpan? quietPeriod = null)
    {
        return new GpuResourceGuard(
            provider,
            messages,
            NullLogger<GpuResourceGuard>.Instance,
            () => config,
            quietPeriod ?? TimeSpan.FromSeconds(5),
            clock);
    }

    private static GpuTranscodeRequest HardwareTranscodeRequest(
        string deviceId = "device-2",
        string playSessionId = "play-2")
    {
        return new GpuTranscodeRequest
        {
            IsVideoRequest = true,
            OutputVideoCodec = "h264",
            CommandLineArguments = CudaNvencArguments,
            SourceWidth = 3840,
            SourceHeight = 2160,
            SourceBitDepth = 10,
            SourceCodec = "hevc",
            SourceRefFrames = 4,
            SourceVideoRangeType = "HDR10",
            OutputWidth = 1920,
            OutputHeight = 1080,
            OutputBitDepth = 8,
            OutputFramerate = 24,
            OutputRefFrames = 4,
            DeviceId = deviceId,
            PlaySessionId = playSessionId,
            UserId = AliceId,
            ItemId = MovieTwoId,
            ItemName = "Movie Two"
        };
    }

    private static GpuTranscodeRequest SmallHardwareTranscodeRequest(
        string deviceId = "device-2",
        string playSessionId = "play-2")
    {
        var request = HardwareTranscodeRequest(deviceId, playSessionId);
        request.CommandLineArguments =
            "-hwaccel cuda -hwaccel_output_format cuda -i source.mkv " +
            "-codec:v:0 h264_nvenc output.m3u8";
        request.SourceWidth = 1920;
        request.SourceHeight = 1080;
        request.SourceBitDepth = 8;
        request.SourceCodec = "h264";
        request.SourceVideoRangeType = "SDR";
        request.OutputWidth = 1920;
        request.OutputHeight = 1080;
        return request;
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
    public async Task IsAdmittedAsync_GladiatorWith1918MiBFreeIsAllowed()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(1918);
        var messages = new RecordingClientMessageService();
        var guard = CreateGuard(EnabledConfig(), provider, messages);
        var request = HardwareTranscodeRequest();
        request.OutputWidth = null;
        request.OutputHeight = null;
        request.OutputBitDepth = null;

        Assert.True(await guard.IsAdmittedAsync(request, CancellationToken.None));
        Assert.Equal(1, provider.QueryCount);
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
        var guard = CreateGuard(EnabledConfig(), provider, messages);

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

        var config = EnabledConfig();
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
        var guard = CreateGuard(EnabledConfig(), provider, messages);

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
        var guard = CreateGuard(EnabledConfig(), provider, messages);

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

        var guard = CreateGuard(EnabledConfig(), provider, messages);

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
        var guard = CreateGuard(EnabledConfig(), provider, messages);

        Assert.True(await guard.IsAdmittedAsync(HardwareTranscodeRequest(), CancellationToken.None));
        Assert.Empty(messages.SentMessages);
    }

    [Fact]
    public async Task IsAdmittedAsync_AbsentGpuIndexFailsOpen()
    {
        var provider = FakeGpuMemoryProvider.Failing("nvidia-smi did not report a usable value for GPU 3");
        var messages = new RecordingClientMessageService();
        var config = EnabledConfig();
        config.GpuIndex = 3;
        var guard = CreateGuard(config, provider, messages);
        var request = HardwareTranscodeRequest();
        request.CommandLineArguments =
            "-hwaccel cuda -hwaccel_output_format cuda -i source.mkv " +
            "-codec:v:0 h264_nvenc output.m3u8";

        Assert.True(await guard.IsAdmittedAsync(request, CancellationToken.None));
        Assert.Equal(3, provider.LastGpuIndex);
        Assert.Empty(messages.SentMessages);
    }

    [Fact]
    public async Task IsAdmittedAsync_QueriesTheGpuSelectedByFfmpeg()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(2500);
        var config = EnabledConfig();
        config.GpuIndex = 0;
        var guard = CreateGuard(config, provider, new RecordingClientMessageService());
        var request = HardwareTranscodeRequest();
        request.CommandLineArguments = request.CommandLineArguments!.Replace("cuda=cu:0", "cuda=cu:2", StringComparison.Ordinal);

        Assert.True(await guard.IsAdmittedAsync(request, CancellationToken.None));
        Assert.Equal(2, provider.LastGpuIndex);
    }

    [Fact]
    public async Task IsAdmittedAsync_ConflictingGpuSelectorsFailOpenWithoutQueryingAnArbitraryDevice()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(10);
        var guard = CreateGuard(EnabledConfig(), provider, new RecordingClientMessageService());
        var request = HardwareTranscodeRequest();
        request.CommandLineArguments = request.CommandLineArguments!
            .Replace("-hwaccel cuda", "-hwaccel cuda -hwaccel_device 1", StringComparison.Ordinal);

        Assert.True(await guard.IsAdmittedAsync(request, CancellationToken.None));
        Assert.Equal(0, provider.QueryCount);
    }

    [Fact]
    public async Task IsAdmittedAsync_DeniesEvenWhenNoSessionCanBeCorrelated()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(700);
        var messages = new RecordingClientMessageService();
        var guard = CreateGuard(EnabledConfig(), provider, messages);

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

        var config = EnabledConfig();
        config.GpuGuardDeniedHeader = string.Empty;
        config.GpuGuardDeniedMessage = "   ";

        var guard = CreateGuard(config, provider, messages);

        Assert.False(await guard.IsAdmittedAsync(HardwareTranscodeRequest(), CancellationToken.None));

        var sent = Assert.Single(messages.SentMessages);
        Assert.False(string.IsNullOrWhiteSpace(sent.Command.Header));
        Assert.False(string.IsNullOrWhiteSpace(sent.Command.Text));
    }

    [Fact]
    public async Task IsAdmittedAsync_RenegotiatedRetriesDoNotEachGetAPopup()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(700);
        var messages = new RecordingClientMessageService();
        messages.AddSession(TestSessions.Create("session-2", "device-2", AliceId));

        // jellyfin-web falls back with setTimeout(..., 100) per hop, so a real burst from one
        // press of play is a few hundred milliseconds end to end.
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var guard = CreateGuard(EnabledConfig(), provider, messages, () => now);

        foreach (var (playSessionId, afterMs) in new[] { ("play-2", 0), ("play-3", 250), ("play-4", 400) })
        {
            now = now.AddMilliseconds(afterMs);
            Assert.False(await guard.IsAdmittedAsync(
                HardwareTranscodeRequest("device-2", playSessionId),
                CancellationToken.None));
        }

        Assert.Single(messages.SentMessages);
    }

    [Fact]
    public async Task IsAdmittedAsync_ASlowBurstStillCollapsesToOnePopup()
    {
        // Headroom check: even at four times the measured fallback delay, a burst is one popup.
        var provider = FakeGpuMemoryProvider.WithFreeMiB(700);
        var messages = new RecordingClientMessageService();
        messages.AddSession(TestSessions.Create("session-2", "device-2", AliceId));

        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var guard = CreateGuard(EnabledConfig(), provider, messages, () => now);

        for (var hop = 0; hop < 4; hop++)
        {
            now = now.AddMilliseconds(hop == 0 ? 0 : 1200);
            await guard.IsAdmittedAsync(
                HardwareTranscodeRequest("device-2", "hop-" + hop),
                CancellationToken.None);
        }

        Assert.Single(messages.SentMessages);
    }

    [Fact]
    public async Task IsAdmittedAsync_ReopeningTheVideoIsAnnouncedEveryTime()
    {
        // The complaint this replaces: nine deliberate re-opens produced one popup, because a
        // fixed window swallowed everything after the first.
        var provider = FakeGpuMemoryProvider.WithFreeMiB(700);
        var messages = new RecordingClientMessageService();
        messages.AddSession(TestSessions.Create("session-2", "device-2", AliceId));

        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var guard = CreateGuard(EnabledConfig(), provider, messages, () => now);

        for (var attempt = 0; attempt < 9; attempt++)
        {
            now = now.AddSeconds(20);
            Assert.False(await guard.IsAdmittedAsync(
                HardwareTranscodeRequest("device-2", "play-" + attempt),
                CancellationToken.None));
        }

        Assert.Equal(9, messages.SentMessages.Count);
    }

    [Fact]
    public async Task IsAdmittedAsync_ABurstFollowedByAReopenGetsExactlyTwoPopups()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(700);
        var messages = new RecordingClientMessageService();
        messages.AddSession(TestSessions.Create("session-2", "device-2", AliceId));

        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var guard = CreateGuard(EnabledConfig(), provider, messages, () => now);

        // Press play: one popup, and the client's two renegotiations stay quiet.
        foreach (var afterMs in new[] { 0, 250, 400 })
        {
            now = now.AddMilliseconds(afterMs);
            await guard.IsAdmittedAsync(HardwareTranscodeRequest("device-2", "burst"), CancellationToken.None);
        }

        // Dismiss the error, press play again.
        now = now.AddSeconds(10);
        await guard.IsAdmittedAsync(HardwareTranscodeRequest("device-2", "reopen"), CancellationToken.None);

        Assert.Equal(2, messages.SentMessages.Count);
    }

    [Fact]
    public async Task IsAdmittedAsync_ADifferentItemOnTheSameDeviceStillGetsItsOwnPopup()
    {
        var provider = FakeGpuMemoryProvider.WithFreeMiB(700);
        var messages = new RecordingClientMessageService();
        messages.AddSession(TestSessions.Create("session-2", "device-2", AliceId));
        var guard = CreateGuard(EnabledConfig(), provider, messages);

        var firstItem = HardwareTranscodeRequest("device-2", "play-2");
        var secondItem = HardwareTranscodeRequest("device-2", "play-3");
        secondItem.ItemId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        await guard.IsAdmittedAsync(firstItem, CancellationToken.None);
        await guard.IsAdmittedAsync(secondItem, CancellationToken.None);

        Assert.Equal(2, messages.SentMessages.Count);
    }

    [Fact]
    public async Task IsAdmittedAsync_StillDeniesWhenTheClientNotificationThrows()
    {
        // Jellyfin ultimately calls raw WebSocket.SendAsync for this path, which can raise an
        // exception ClientMessageService does not catch. A failed popup must never become an
        // admission for a transcode already judged unsafe.
        var provider = FakeGpuMemoryProvider.WithFreeMiB(700);
        var messages = new ThrowingClientMessageService(TestSessions.Create("session-2", "device-2", AliceId));

        var guard = new GpuResourceGuard(
            provider,
            messages,
            NullLogger<GpuResourceGuard>.Instance,
            () => EnabledConfig());

        Assert.False(await guard.IsAdmittedAsync(HardwareTranscodeRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task IsAdmittedAsync_TakesAFreshReadingForEveryAdmission()
    {
        // Two transcodes arriving close together must not share one pre-allocation reading:
        // the second sees the memory the first consumed and is refused.
        var provider = new SequencedGpuMemoryProvider(2500, 700);
        var messages = new RecordingClientMessageService();
        messages.AddSession(TestSessions.Create("session-1", "device-1", AliceId));
        messages.AddSession(TestSessions.Create("session-2", "device-2", AliceId));
        var guard = CreateGuard(EnabledConfig(), provider, messages);

        var first = await guard.IsAdmittedAsync(HardwareTranscodeRequest("device-1", "play-1"), CancellationToken.None);
        var second = await guard.IsAdmittedAsync(HardwareTranscodeRequest("device-2", "play-2"), CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(2, provider.QueryCount);
    }

    [Fact]
    public async Task TryReserveAsync_ConcurrentAdmissionsCannotSpendTheSameFreeMemory()
    {
        // Both queries see the same pre-allocation reading. The first admission's job budget
        // is reserved before the second decision, so only one can launch.
        var provider = new SequencedGpuMemoryProvider(2000, 2000);
        var messages = new RecordingClientMessageService();
        var guard = CreateGuard(EnabledConfig(), provider, messages);
        var firstRequest = HardwareTranscodeRequest("device-1", "play-1");
        firstRequest.OutputPath = "/transcodes/one.m3u8";
        var secondRequest = HardwareTranscodeRequest("device-2", "play-2");
        secondRequest.OutputPath = "/transcodes/two.m3u8";

        var first = await guard.TryReserveAsync(firstRequest, CancellationToken.None);
        var second = await guard.TryReserveAsync(secondRequest, CancellationToken.None);

        Assert.True(first.IsAdmitted);
        Assert.NotNull(first.Reservation);
        Assert.False(second.IsAdmitted);
        Assert.Equal(2, provider.QueryCount);
        first.Reservation.Dispose();
    }

    [Fact]
    public async Task TryReserveAsync_SerializesAnActuallyOverlappingReadAndReservation()
    {
        var provider = new BlockingFirstGpuMemoryProvider(2000);
        var guard = CreateGuard(EnabledConfig(), provider, new RecordingClientMessageService());
        var firstRequest = HardwareTranscodeRequest("device-1", "play-1");
        firstRequest.OutputPath = "/transcodes/one.m3u8";
        var secondRequest = HardwareTranscodeRequest("device-2", "play-2");
        secondRequest.OutputPath = "/transcodes/two.m3u8";

        var firstTask = guard.TryReserveAsync(firstRequest, CancellationToken.None);
        await provider.FirstQueryStarted;
        var secondTask = guard.TryReserveAsync(secondRequest, CancellationToken.None);

        // Async methods run synchronously to their first incomplete await. The second call has
        // reached the occupied admission gate, so it cannot have queried yet.
        Assert.Equal(1, provider.QueryCount);

        provider.ReleaseFirstQuery();
        var first = await firstTask;
        var second = await secondTask;

        Assert.True(first.IsAdmitted);
        Assert.False(second.IsAdmitted);
        Assert.Equal(2, provider.QueryCount);
        first.Reservation!.Dispose();
    }

    [Fact]
    public async Task TryReserveAsync_QueryFailureLaunchIsStillReservedForTheNextAdmission()
    {
        // Fail open for this request, but do not let a successful query immediately afterwards
        // spend memory that the just-launched FFmpeg process has not exposed to nvidia-smi yet.
        var provider = new SequencedGpuMemoryProvider(
            GpuMemoryQueryResult.Failed("temporary failure"),
            GpuMemoryQueryResult.FromFreeMiB(600));
        var guard = CreateGuard(EnabledConfig(), provider, new RecordingClientMessageService());
        var firstRequest = SmallHardwareTranscodeRequest("device-1", "play-1");
        firstRequest.OutputPath = "/transcodes/one.m3u8";
        var secondRequest = SmallHardwareTranscodeRequest("device-2", "play-2");
        secondRequest.OutputPath = "/transcodes/two.m3u8";

        var first = await guard.TryReserveAsync(firstRequest, CancellationToken.None);
        var second = await guard.TryReserveAsync(secondRequest, CancellationToken.None);

        Assert.True(first.IsAdmitted);
        Assert.NotNull(first.Reservation);
        Assert.False(second.IsAdmitted);
        first.Reservation.Dispose();
    }

    [Fact]
    public async Task TryReserveAsync_LaunchedBudgetExpiresAfterTheRaceWindow()
    {
        // A free-memory drop cannot prove which process allocated it: Gemma may be growing beside
        // Jellyfin. Keep the reservation for the full race window, then trust fresh readings.
        var provider = new SequencedGpuMemoryProvider(600, 600, 600);
        var messages = new RecordingClientMessageService();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var guard = CreateGuard(EnabledConfig(), provider, messages, () => now);
        var firstRequest = SmallHardwareTranscodeRequest("device-1", "play-1");
        var secondRequest = SmallHardwareTranscodeRequest("device-2", "play-2");
        var thirdRequest = SmallHardwareTranscodeRequest("device-3", "play-3");

        var first = await guard.TryReserveAsync(firstRequest, CancellationToken.None);
        Assert.NotNull(first.Reservation);
        await first.Reservation.MarkLaunched();

        now = now.AddSeconds(2);
        var second = await guard.TryReserveAsync(secondRequest, CancellationToken.None);
        now = now.AddSeconds(2);
        var third = await guard.TryReserveAsync(thirdRequest, CancellationToken.None);

        Assert.False(second.IsAdmitted);
        Assert.True(third.IsAdmitted);
        Assert.NotNull(third.Reservation);
        third.Reservation.Dispose();
    }

    [Fact]
    public async Task TryReserveAsync_ProcessSpecificAllocationReleasesOnlyItsOwnReservationEarly()
    {
        // The first free reading is before launch. The second already includes the observed
        // 323-MiB FFmpeg allocation, so its temporary budget must no longer be subtracted too.
        var provider = new ObservingGpuMemoryProvider(processUsedMiB: 323, 1112, 600);
        var guard = CreateGuard(EnabledConfig(), provider, new RecordingClientMessageService());
        var firstRequest = SmallHardwareTranscodeRequest("device-1", "play-1");
        firstRequest.OutputPath = "/transcodes/one.m3u8";
        var secondRequest = SmallHardwareTranscodeRequest("device-2", "play-2");
        secondRequest.OutputPath = "/transcodes/two.m3u8";

        var first = await guard.TryReserveAsync(firstRequest, CancellationToken.None);
        await first.Reservation!.MarkLaunched(Environment.ProcessId, firstRequest);

        var second = await guard.TryReserveAsync(secondRequest, CancellationToken.None);

        Assert.True(second.IsAdmitted);
        second.Reservation!.Dispose();
    }

    [Fact]
    public async Task TryReserveAsync_VisibleOutputJobAllowsItsDuplicateWithoutBudgetingANewProcess()
    {
        var provider = new ObservingGpuMemoryProvider(processUsedMiB: 323, 600, 0);
        var guard = CreateGuard(EnabledConfig(), provider, new RecordingClientMessageService());
        var request = SmallHardwareTranscodeRequest("device-1", "play-1");
        request.OutputPath = "/transcodes/one.m3u8";

        var first = await guard.TryReserveAsync(request, CancellationToken.None);
        await first.Reservation!.MarkLaunched(Environment.ProcessId, request);
        var duplicate = await guard.TryReserveAsync(request, CancellationToken.None);

        Assert.True(duplicate.IsAdmitted);
        Assert.NotNull(duplicate.Reservation);
        duplicate.Reservation.Dispose();
    }

    [Fact]
    public async Task TryReserveAsync_StaleProcessObservationCannotReleaseAReusedPathReservation()
    {
        var provider = new DelayedObservingGpuMemoryProvider(600, 600, 600);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var guard = CreateGuard(
            EnabledConfig(),
            provider,
            new RecordingClientMessageService(),
            () => now);
        var oldRequest = SmallHardwareTranscodeRequest("device-1", "play-1");
        oldRequest.OutputPath = "/transcodes/reused.m3u8";

        var oldAttempt = await guard.TryReserveAsync(oldRequest, CancellationToken.None);
        var oldObservation = oldAttempt.Reservation!.MarkLaunched(Environment.ProcessId, oldRequest);
        await provider.ProcessQueryStarted;

        // Expire and recreate the same path while the old PID query is still pending.
        now = now.AddSeconds(4);
        var replacement = await guard.TryReserveAsync(oldRequest, CancellationToken.None);
        Assert.True(replacement.IsAdmitted);

        provider.ReleaseProcessQuery();
        await oldObservation;

        var otherRequest = SmallHardwareTranscodeRequest("device-2", "play-2");
        otherRequest.OutputPath = "/transcodes/other.m3u8";
        var other = await guard.TryReserveAsync(otherRequest, CancellationToken.None);

        Assert.False(other.IsAdmitted);
        replacement.Reservation!.Dispose();
    }

    [Fact]
    public async Task TryReserveAsync_DuplicateStartForOneOutputPathUsesOneBudget()
    {
        // Jellyfin can call StartFfMpeg more than once for one playlist. The server-derived output
        // path identifies that one FFmpeg allocation even if client metadata changes.
        var provider = new SequencedGpuMemoryProvider(600, 0);
        var messages = new RecordingClientMessageService();
        var guard = CreateGuard(EnabledConfig(), provider, messages);
        var initSegment = SmallHardwareTranscodeRequest("device-1", "play-1");
        initSegment.OutputPath = "/transcodes/session/main.m3u8";
        var mediaSegment = SmallHardwareTranscodeRequest("device-1", "client-reused-a-different-id");
        mediaSegment.OutputPath = "/transcodes/session/main.m3u8";

        var first = await guard.TryReserveAsync(initSegment, CancellationToken.None);
        var second = await guard.TryReserveAsync(mediaSegment, CancellationToken.None);

        Assert.True(first.IsAdmitted);
        Assert.True(second.IsAdmitted);
        first.Reservation!.Dispose();
        second.Reservation!.Dispose();
    }

    [Fact]
    public async Task TryReserveAsync_SamePlaySessionCannotMergeDistinctOutputJobs()
    {
        var provider = new SequencedGpuMemoryProvider(600, 600);
        var guard = CreateGuard(EnabledConfig(), provider, new RecordingClientMessageService());
        var firstRequest = SmallHardwareTranscodeRequest("device-1", "client-chosen-id");
        firstRequest.OutputPath = "/transcodes/one.m3u8";
        var secondRequest = SmallHardwareTranscodeRequest("device-1", "client-chosen-id");
        secondRequest.OutputPath = "/transcodes/two.m3u8";

        var first = await guard.TryReserveAsync(firstRequest, CancellationToken.None);
        var second = await guard.TryReserveAsync(secondRequest, CancellationToken.None);

        Assert.True(first.IsAdmitted);
        Assert.False(second.IsAdmitted);
        first.Reservation!.Dispose();
    }

    [Fact]
    public async Task TryReserveAsync_ThrowingLoggerCannotLoseAnAllowedReservation()
    {
        var provider = new SequencedGpuMemoryProvider(600, 600);
        var guard = new GpuResourceGuard(
            provider,
            new RecordingClientMessageService(),
            new ThrowingLogger<GpuResourceGuard>(),
            () => EnabledConfig());
        var firstRequest = SmallHardwareTranscodeRequest("device-1", "play-1");
        firstRequest.OutputPath = "/transcodes/one.m3u8";
        var secondRequest = SmallHardwareTranscodeRequest("device-2", "play-2");
        secondRequest.OutputPath = "/transcodes/two.m3u8";

        var first = await guard.TryReserveAsync(firstRequest, CancellationToken.None);
        var second = await guard.TryReserveAsync(secondRequest, CancellationToken.None);

        Assert.True(first.IsAdmitted);
        Assert.NotNull(first.Reservation);
        Assert.False(second.IsAdmitted);
        first.Reservation.Dispose();
    }

    [Fact]
    public async Task IsAdmittedAsync_ThrowingLoggerCannotReverseADenial()
    {
        var messages = new RecordingClientMessageService();
        messages.AddSession(TestSessions.Create("session-2", "device-2", AliceId));
        var guard = new GpuResourceGuard(
            FakeGpuMemoryProvider.WithFreeMiB(10),
            messages,
            new ThrowingLogger<GpuResourceGuard>(),
            () => EnabledConfig());

        Assert.False(await guard.IsAdmittedAsync(HardwareTranscodeRequest(), CancellationToken.None));
        Assert.Single(messages.SentMessages);
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
