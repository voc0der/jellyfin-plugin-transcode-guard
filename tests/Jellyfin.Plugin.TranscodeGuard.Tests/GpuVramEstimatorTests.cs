using Jellyfin.Plugin.TranscodeGuard.Gpu;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

public class GpuVramEstimatorTests
{
    private const string NvencArguments =
        "-hwaccel cuda -hwaccel_output_format cuda -i source.mkv -codec:v:0 h264_nvenc output.m3u8";

    private const string TonemapArguments =
        "-hwaccel cuda -hwaccel_output_format cuda -i source.mkv " +
        "-vf \"tonemap_cuda=format=yuv420p,scale_cuda=1920:1080\" -codec:v:0 h264_nvenc output.m3u8";

    private static GpuTranscodeRequest VideoRequest() => new()
    {
        IsVideoRequest = true,
        OutputVideoCodec = "h264",
        CommandLineArguments = NvencArguments,
        SourceWidth = 1920,
        SourceHeight = 1080,
        SourceBitDepth = 8,
        SourceCodec = "h264",
        SourceRefFrames = 4,
        SourceFramerate = 24,
        SourcePixelFormat = "yuv420p",
        OutputWidth = 1920,
        OutputHeight = 1080,
        OutputBitDepth = 8,
        OutputFramerate = 24,
        OutputRefFrames = 4
    };

    [Fact]
    public void Estimate_Small1080pJobUsesHalfGiBBudget()
    {
        var estimate = GpuVramEstimator.Estimate(VideoRequest());

        Assert.Equal(512, estimate.BudgetMiB);
        Assert.False(estimate.UsedFallbackMetadata);
    }

    [Fact]
    public void Estimate_4kHdrTonemapMatchesThe1381MiBMeasurement()
    {
        var request = VideoRequest();
        request.CommandLineArguments = TonemapArguments;
        request.SourceWidth = 3840;
        request.SourceHeight = 2160;
        request.SourceBitDepth = 10;
        request.SourceCodec = "hevc";
        request.SourceVideoRangeType = "HDR10";
        request.OutputWidth = 3840;
        request.OutputHeight = 2160;

        var estimate = GpuVramEstimator.Estimate(request);

        Assert.Equal(1408, estimate.BudgetMiB);
        Assert.True(estimate.UsesTonemap);
    }

    [Fact]
    public void Estimate_4kGladiatorShapeWithMissingOutputMetadataUsesCompleteMeasuredEnvelope()
    {
        var request = VideoRequest();
        request.CommandLineArguments = TonemapArguments;
        request.SourceWidth = 3840;
        request.SourceHeight = 2160;
        request.SourceBitDepth = 10;
        request.SourceCodec = "hevc";
        request.SourceVideoRangeType = "HDR10";
        request.OutputWidth = null;
        request.OutputHeight = null;
        request.OutputBitDepth = null;

        var estimate = GpuVramEstimator.Estimate(request);

        Assert.Equal(1408, estimate.BudgetMiB);
        Assert.True(estimate.UsedFallbackMetadata);
    }

    [Fact]
    public void Estimate_4kPipelinePressureSignalsDoNotDoubleCountTheSameSurfacePool()
    {
        var request = VideoRequest();
        request.CommandLineArguments =
            "-hwaccel cuda -i source.mkv " +
            "-vf \"tonemap_cuda,scale_cuda=1920:1080,overlay_cuda\" " +
            "-codec:v:0 av1_nvenc -refs:v:0 12 -r:v:0 120 output.m3u8";
        request.OutputVideoCodec = "av1";
        request.SourceWidth = 3840;
        request.SourceHeight = 2160;
        request.SourceBitDepth = 10;
        request.OutputWidth = null;
        request.OutputHeight = null;
        request.OutputRefFrames = null;
        request.OutputFramerate = null;

        Assert.Equal(1408, GpuVramEstimator.Estimate(request).BudgetMiB);
    }

    [Fact]
    public void Estimate_4k10BitWithoutTonemapMatchesThe824MiBMeasurement()
    {
        var request = VideoRequest();
        request.SourceWidth = 3840;
        request.SourceHeight = 2160;
        request.SourceBitDepth = 10;
        request.SourceCodec = "hevc";
        request.SourceVideoRangeType = "HDR10";
        request.OutputWidth = 3840;
        request.OutputHeight = 2160;

        var estimate = GpuVramEstimator.Estimate(request);

        Assert.Equal(896, estimate.BudgetMiB);
        Assert.False(estimate.UsesTonemap);
    }

    [Fact]
    public void Estimate_4kTo1080pScaleCostsMoreThanNative1080p()
    {
        var native1080p = VideoRequest();
        var downscale4k = VideoRequest();
        downscale4k.CommandLineArguments =
            "-hwaccel cuda -hwaccel_output_format cuda -i source.mkv " +
            "-vf scale_cuda=1920:1080 -codec:v:0 h264_nvenc output.m3u8";
        downscale4k.SourceWidth = 3840;
        downscale4k.SourceHeight = 2160;

        var nativeEstimate = GpuVramEstimator.Estimate(native1080p);
        var downscaleEstimate = GpuVramEstimator.Estimate(downscale4k);

        Assert.Equal(512, nativeEstimate.BudgetMiB);
        Assert.Equal(768, downscaleEstimate.BudgetMiB);
    }

    [Fact]
    public void Estimate_AudioOnlyUsesNoGpuBudget()
    {
        var request = VideoRequest();
        request.IsVideoRequest = false;
        request.OutputVideoCodec = null;

        Assert.Equal(0, GpuVramEstimator.Estimate(request).BudgetMiB);
    }

    [Fact]
    public void Estimate_RemuxUsesNoGpuBudget()
    {
        var request = VideoRequest();
        request.OutputVideoCodec = "copy";

        Assert.Equal(0, GpuVramEstimator.Estimate(request).BudgetMiB);
    }

    [Fact]
    public void Estimate_MissingMetadataFallsBackConservatively()
    {
        var request = new GpuTranscodeRequest
        {
            IsVideoRequest = true,
            OutputVideoCodec = "h264",
            CommandLineArguments = NvencArguments
        };

        var estimate = GpuVramEstimator.Estimate(request);

        Assert.True(estimate.BudgetMiB >= 896);
        Assert.True(estimate.UsedFallbackMetadata);
    }

    [Fact]
    public void Estimate_8k10BitScalesBeyondTheOld4GiBCap()
    {
        var request = VideoRequest();
        request.SourceWidth = 7680;
        request.SourceHeight = 4320;
        request.SourceBitDepth = 10;
        request.OutputWidth = 7680;
        request.OutputHeight = 4320;
        request.OutputBitDepth = 10;

        Assert.Equal(3584, GpuVramEstimator.Estimate(request).BudgetMiB);

        request.CommandLineArguments = TonemapArguments;
        Assert.Equal(5632, GpuVramEstimator.Estimate(request).BudgetMiB);
    }

    [Fact]
    public void Estimate_8kSourceStillScalesWhenOutputMetadataIsMissing()
    {
        var request = VideoRequest();
        request.SourceWidth = 7680;
        request.SourceHeight = 4320;
        request.SourceBitDepth = 10;
        request.OutputWidth = null;
        request.OutputHeight = null;

        var estimate = GpuVramEstimator.Estimate(request);

        Assert.Equal(3584, estimate.BudgetMiB);
        Assert.True(estimate.UsedFallbackMetadata);
    }

    [Fact]
    public void Estimate_HighSourceFramerateIsCountedWhenNoTargetCapExists()
    {
        var request = VideoRequest();
        request.SourceFramerate = 120;
        request.OutputFramerate = null;

        Assert.Equal(768, GpuVramEstimator.Estimate(request).BudgetMiB);
    }

    [Fact]
    public void Estimate_InfersHighOutputReferencePressureFromFfmpegArguments()
    {
        var request = VideoRequest();
        request.OutputRefFrames = null;
        request.CommandLineArguments =
            "-hwaccel cuda -i source.mkv -codec:v:0 h264_nvenc -refs:v:0 12 output.m3u8";

        Assert.Equal(768, GpuVramEstimator.Estimate(request).BudgetMiB);
    }

    [Fact]
    public void Estimate_InfersHighOutputFramerateFromFfmpegArguments()
    {
        var request = VideoRequest();
        request.OutputFramerate = 24;
        request.CommandLineArguments =
            "-hwaccel cuda -i source.mkv -codec:v:0 h264_nvenc -r:v:0 120/1 output.m3u8";

        Assert.Equal(768, GpuVramEstimator.Estimate(request).BudgetMiB);
    }

    [Fact]
    public void Estimate_InfersTenBitOutputFromTheFfmpegPixelFormat()
    {
        var request = VideoRequest();
        request.SourceWidth = 3840;
        request.SourceHeight = 2160;
        request.SourceBitDepth = 8;
        request.OutputWidth = 3840;
        request.OutputHeight = 2160;
        request.OutputBitDepth = null;
        request.CommandLineArguments =
            "-hwaccel cuda -i source.mkv -pix_fmt p010le -codec:v:0 hevc_nvenc output.m3u8";

        Assert.Equal(896, GpuVramEstimator.Estimate(request).BudgetMiB);
    }

    [Fact]
    public void Estimate_AccountsForAdditionalCudaFilterSurfaces()
    {
        var request = VideoRequest();
        request.CommandLineArguments =
            "-hwaccel cuda -i source.mkv -vf \"hwupload_cuda,overlay_cuda\" " +
            "-codec:v:0 h264_nvenc output.m3u8";

        Assert.Equal(768, GpuVramEstimator.Estimate(request).BudgetMiB);
    }

    [Fact]
    public void Estimate_InfersMissingSourceDepthFromPixelFormat()
    {
        var request = VideoRequest();
        request.SourceBitDepth = null;
        request.SourcePixelFormat = "yuv420p10le";

        var estimate = GpuVramEstimator.Estimate(request);

        Assert.Equal(512, estimate.BudgetMiB);
        Assert.False(estimate.UsedFallbackMetadata);
        Assert.Equal(10, estimate.SourceBitDepth);
    }

    [Fact]
    public void Estimate_ExtremeDimensionsSaturateWithoutOverflowing()
    {
        var request = VideoRequest();
        request.SourceWidth = int.MaxValue;
        request.SourceHeight = int.MaxValue;

        Assert.Equal(int.MaxValue, GpuVramEstimator.Estimate(request).BudgetMiB);
    }
}
