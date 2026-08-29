using Jellyfin.Plugin.TranscodeNag.Configuration;
using Jellyfin.Plugin.TranscodeNag.Gpu;

namespace Jellyfin.Plugin.TranscodeNag.Tests;

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
    public void ScaleBudgetMiB_DefaultPercentageLeavesTheModelBudgetAlone()
    {
        Assert.Equal(100, new PluginConfiguration().GpuVramBudgetPercent);
        Assert.Equal(1536, GpuAdmissionPolicy.ScaleBudgetMiB(1536, EnabledConfig()));
    }

    [Fact]
    public void ScaleBudgetMiB_LoweringThePercentageAdmitsAJobTheModelBudgetWouldRefuse()
    {
        // The reported case: a 4K HDR tone-mapped job budgeted at 1536 MiB against 1490 MiB free.
        var config = EnabledConfig();
        config.GpuVramBudgetPercent = 90;

        var budgetMiB = GpuAdmissionPolicy.ScaleBudgetMiB(1536, config);

        Assert.Equal(1382, budgetMiB);
        Assert.Equal(
            GpuAdmissionOutcome.AllowedSufficientMemory,
            GpuAdmissionPolicy.Evaluate(
                config,
                requiresGpuVideoTranscode: true,
                memory: GpuMemoryQueryResult.FromFreeMiB(1490),
                jobBudgetMiB: budgetMiB));
    }

    [Fact]
    public void ScaleBudgetMiB_RaisingThePercentageBuysMargin()
    {
        var config = EnabledConfig();
        config.GpuVramBudgetPercent = 125;

        Assert.Equal(1920, GpuAdmissionPolicy.ScaleBudgetMiB(1536, config));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    [InlineData(int.MinValue)]
    public void ScaleBudgetMiB_PercentagesBelowTheFloorAreClamped(int percent)
    {
        // A stored configuration need not have come from this build's settings page.
        var config = EnabledConfig();
        config.GpuVramBudgetPercent = percent;

        Assert.Equal(
            1536 * GpuAdmissionPolicy.MinimumBudgetPercent / 100,
            GpuAdmissionPolicy.ScaleBudgetMiB(1536, config));
    }

    [Fact]
    public void ScaleBudgetMiB_PercentagesAboveTheCeilingAreClamped()
    {
        var config = EnabledConfig();
        config.GpuVramBudgetPercent = int.MaxValue;

        Assert.Equal(
            1536 * GpuAdmissionPolicy.MaximumBudgetPercent / 100,
            GpuAdmissionPolicy.ScaleBudgetMiB(1536, config));
    }

    [Fact]
    public void ScaleBudgetMiB_AJobThatNeedsVramNeverScalesDownToFree()
    {
        var config = EnabledConfig();
        config.GpuVramBudgetPercent = GpuAdmissionPolicy.MinimumBudgetPercent;

        Assert.Equal(1, GpuAdmissionPolicy.ScaleBudgetMiB(1, config));
        Assert.Equal(0, GpuAdmissionPolicy.ScaleBudgetMiB(0, config));
    }

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
