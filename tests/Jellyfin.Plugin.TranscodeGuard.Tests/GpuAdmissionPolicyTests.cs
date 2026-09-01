using Jellyfin.Plugin.TranscodeGuard.Configuration;
using Jellyfin.Plugin.TranscodeGuard.Gpu;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

public class GpuAdmissionPolicyTests
{
    private const string CudaNvencArguments =
        "-init_hw_device cuda=cu:0 -hwaccel cuda -hwaccel_output_format cuda -i \"/media/movie.mkv\" " +
        "-codec:v:0 av1_nvenc -codec:a:0 libfdk_aac";

    private static PluginConfiguration EnabledConfig() => new()
    {
        EnableGpuResourceGuard = true,
        GpuIndex = 0
    };

    [Fact]
    public void Evaluate_GuardDisabled_AllowsWithoutNeedingAGpuReading()
    {
        var config = new PluginConfiguration { EnableGpuResourceGuard = false };

        Assert.False(GpuAdmissionPolicy.RequiresGpuQuery(config, requiresGpuVideoTranscode: true));
        Assert.Equal(
            GpuAdmissionOutcome.AllowedGuardDisabled,
            GpuAdmissionPolicy.Evaluate(
                config,
                requiresGpuVideoTranscode: true,
                memory: null,
                jobBudgetMiB: 1536));
    }

    [Fact]
    public void RequiresGpuVideoTranscode_DirectPlayHasNoFfmpegJobToJudge()
    {
        // Direct Play never reaches StartFfMpeg at all; the closest analogue here is a request
        // that carries neither a video encode nor GPU flags.
        Assert.False(GpuAdmissionPolicy.RequiresGpuVideoTranscode(
            isVideoRequest: false,
            outputVideoCodec: null,
            commandLineArguments: null));
    }

    [Fact]
    public void RequiresGpuVideoTranscode_RemuxIsAllowedEvenWithGpuFlagsPresent()
    {
        Assert.False(GpuAdmissionPolicy.RequiresGpuVideoTranscode(
            isVideoRequest: true,
            outputVideoCodec: "copy",
            commandLineArguments: CudaNvencArguments));
    }

    [Fact]
    public void RequiresGpuVideoTranscode_AudioOnlyTranscodeIsAllowed()
    {
        Assert.False(GpuAdmissionPolicy.RequiresGpuVideoTranscode(
            isVideoRequest: false,
            outputVideoCodec: null,
            commandLineArguments: "-i \"/media/song.flac\" -codec:a:0 libmp3lame -f mp3 out.mp3"));
    }

    [Fact]
    public void RequiresGpuVideoTranscode_CpuVideoTranscodeIsAllowed()
    {
        Assert.False(GpuAdmissionPolicy.RequiresGpuVideoTranscode(
            isVideoRequest: true,
            outputVideoCodec: "h264",
            commandLineArguments: "-i \"/media/movie.mkv\" -codec:v:0 libx264 -codec:a:0 libfdk_aac"));
    }

    [Fact]
    public void RequiresGpuVideoTranscode_HardwareVideoTranscodeIsGuarded()
    {
        Assert.True(GpuAdmissionPolicy.RequiresGpuVideoTranscode(
            isVideoRequest: true,
            outputVideoCodec: "av1",
            commandLineArguments: CudaNvencArguments));
    }

    [Fact]
    public void Evaluate_NonGpuTranscodeIsAllowedRegardlessOfFreeMemory()
    {
        var outcome = GpuAdmissionPolicy.Evaluate(
            EnabledConfig(),
            requiresGpuVideoTranscode: false,
            memory: GpuMemoryQueryResult.FromFreeMiB(1),
            jobBudgetMiB: 0);

        Assert.Equal(GpuAdmissionOutcome.AllowedNotGpuTranscode, outcome);
    }

    [Fact]
    public void Evaluate_SufficientVramAllows()
    {
        var outcome = GpuAdmissionPolicy.Evaluate(
            EnabledConfig(),
            requiresGpuVideoTranscode: true,
            memory: GpuMemoryQueryResult.FromFreeMiB(1918),
            jobBudgetMiB: 1536);

        Assert.Equal(GpuAdmissionOutcome.AllowedSufficientMemory, outcome);
    }

    [Fact]
    public void Evaluate_InsufficientVramDenies()
    {
        var outcome = GpuAdmissionPolicy.Evaluate(
            EnabledConfig(),
            requiresGpuVideoTranscode: true,
            memory: GpuMemoryQueryResult.FromFreeMiB(1400),
            jobBudgetMiB: 1536);

        Assert.Equal(GpuAdmissionOutcome.Denied, outcome);
    }

    [Fact]
    public void Evaluate_ExactlyAtThresholdAllows()
    {
        var outcome = GpuAdmissionPolicy.Evaluate(
            EnabledConfig(),
            requiresGpuVideoTranscode: true,
            memory: GpuMemoryQueryResult.FromFreeMiB(1536),
            jobBudgetMiB: 1536);

        Assert.Equal(GpuAdmissionOutcome.AllowedSufficientMemory, outcome);
    }

    [Fact]
    public void Evaluate_FailedQueryFailsOpen()
    {
        var outcome = GpuAdmissionPolicy.Evaluate(
            EnabledConfig(),
            requiresGpuVideoTranscode: true,
            memory: GpuMemoryQueryResult.Failed("nvidia-smi is not available"),
            jobBudgetMiB: 1536);

        Assert.Equal(GpuAdmissionOutcome.AllowedQueryFailed, outcome);
    }

    [Fact]
    public void Evaluate_MissingReadingFailsOpen()
    {
        var outcome = GpuAdmissionPolicy.Evaluate(
            EnabledConfig(),
            requiresGpuVideoTranscode: true,
            memory: null,
            jobBudgetMiB: 1536);

        Assert.Equal(GpuAdmissionOutcome.AllowedQueryFailed, outcome);
    }

    [Fact]
    public void DefaultConfiguration_LeavesTheGuardOff()
    {
        var config = new PluginConfiguration();

        Assert.False(config.EnableGpuResourceGuard);
        Assert.Equal(0, config.GpuIndex);
        Assert.Equal(1000, config.GpuCheckTimeoutMilliseconds);
    }

    [Fact]
    public void Evaluate_InFlightBudgetPreventsTwoJobsSpendingTheSameReading()
    {
        var outcome = GpuAdmissionPolicy.Evaluate(
            EnabledConfig(),
            requiresGpuVideoTranscode: true,
            memory: GpuMemoryQueryResult.FromFreeMiB(2000),
            jobBudgetMiB: 1536,
            inFlightBudgetMiB: 512);

        Assert.Equal(GpuAdmissionOutcome.Denied, outcome);
    }

    [Theory]
    [InlineData(1918, 1536)]
    [InlineData(1089, 1024)]
    [InlineData(573, 512)]
    public void Evaluate_CoarseBudgetFitsObservedFreeVram(int freeMiB, int jobBudgetMiB)
    {
        var outcome = GpuAdmissionPolicy.Evaluate(
            EnabledConfig(),
            requiresGpuVideoTranscode: true,
            memory: GpuMemoryQueryResult.FromFreeMiB(freeMiB),
            jobBudgetMiB: jobBudgetMiB);

        Assert.Equal(GpuAdmissionOutcome.AllowedSufficientMemory, outcome);
    }
}
