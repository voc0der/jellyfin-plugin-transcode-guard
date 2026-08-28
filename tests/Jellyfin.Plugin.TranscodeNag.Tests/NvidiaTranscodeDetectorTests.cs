using Jellyfin.Plugin.TranscodeNag.Gpu;

namespace Jellyfin.Plugin.TranscodeNag.Tests;

public class NvidiaTranscodeDetectorTests
{
    // Trimmed from the 2160p HDR -> AV1 NVENC job in the reported reproduction.
    private const string CudaAv1NvencArguments =
        "-analyzeduration 200M -init_hw_device cuda=cu:0 -filter_hw_device cu -hwaccel cuda " +
        "-hwaccel_output_format cuda -noautorotate -i \"/media/movies/Some Movie (2019)/Some Movie.mkv\" " +
        "-map 0:0 -map 0:1 -codec:v:0 av1_nvenc -preset p1 " +
        "-vf \"setparams=color_primaries=bt2020,tonemap_cuda=format=yuv420p:p=bt709,scale_cuda=w=1920:h=1080\" " +
        "-codec:a:0 libfdk_aac -f hls \"/config/transcodes/abc.m3u8\"";

    private const string CpuLibx264Arguments =
        "-analyzeduration 200M -noautorotate -i \"/media/movies/Some Movie.mkv\" -map 0:0 -map 0:1 " +
        "-codec:v:0 libx264 -preset veryfast -crf 23 -vf \"scale=trunc(min(max(iw\\,ih*dar)\\,1920)/2)*2:-2\" " +
        "-codec:a:0 libfdk_aac -f hls \"/config/transcodes/abc.m3u8\"";

    private const string RemuxArguments =
        "-i \"/media/movies/Some Movie.mkv\" -map 0:0 -map 0:1 -codec:v:0 copy -codec:a:0 libfdk_aac " +
        "-f hls \"/config/transcodes/abc.m3u8\"";

    [Fact]
    public void UsesNvidiaGpu_DetectsCudaAv1NvencJob()
    {
        Assert.True(NvidiaTranscodeDetector.UsesNvidiaGpu(CudaAv1NvencArguments));
    }

    [Fact]
    public void UsesNvidiaGpu_IgnoresCpuOnlyTranscode()
    {
        Assert.False(NvidiaTranscodeDetector.UsesNvidiaGpu(CpuLibx264Arguments));
    }

    [Fact]
    public void UsesNvidiaGpu_IgnoresStreamCopy()
    {
        Assert.False(NvidiaTranscodeDetector.UsesNvidiaGpu(RemuxArguments));
    }

    [Theory]
    [InlineData("-hwaccel cuda -i in.mkv -c:v h264_nvenc out.ts")]
    [InlineData("-hwaccel nvdec -i in.mkv -c:v libx264 out.ts")]
    [InlineData("-i in.mkv -codec:v:0 hevc_nvenc out.ts")]
    [InlineData("-c:v h264_cuvid -i in.mkv -c:v libx264 out.ts")]
    [InlineData("-init_hw_device cuda -i in.mkv -c:v libx264 out.ts")]
    [InlineData("-hwaccel_output_format cuda -i in.mkv -c:v libx264 out.ts")]
    [InlineData("-i in.mkv -vf \"hwupload_cuda,scale_npp=w=1920:h=1080\" -c:v libx264 out.ts")]
    public void UsesNvidiaGpu_DetectsEachNvidiaMarker(string arguments)
    {
        Assert.True(NvidiaTranscodeDetector.UsesNvidiaGpu(arguments));
    }

    [Theory]
    [InlineData("-hwaccel qsv -i in.mkv -c:v h264_qsv out.ts")]
    [InlineData("-hwaccel vaapi -i in.mkv -c:v h264_vaapi out.ts")]
    [InlineData("-hwaccel videotoolbox -i in.mkv -c:v h264_videotoolbox out.ts")]
    public void UsesNvidiaGpu_IgnoresOtherVendorsHardwarePaths(string arguments)
    {
        // Only the protected NVIDIA path is guarded; other accelerators are out of scope.
        Assert.False(NvidiaTranscodeDetector.UsesNvidiaGpu(arguments));
    }

    [Fact]
    public void UsesNvidiaGpu_IsNotTrippedByMediaFileNames()
    {
        const string arguments =
            "-i \"/media/movies/Cuda (2019)/Cuda nvenc _cuda scale_npp.mkv\" -c:v libx264 -f hls \"/config/cuda.m3u8\"";

        Assert.False(NvidiaTranscodeDetector.UsesNvidiaGpu(arguments));
    }

    [Fact]
    public void UsesNvidiaGpu_IsNotTrippedBySubtitlePathsInsideFilterGraphs()
    {
        const string arguments =
            "-i in.mkv -vf \"subtitles=filename='/media/show_cuda.srt',scale=1280:720\" -c:v libx264 out.ts";

        Assert.False(NvidiaTranscodeDetector.UsesNvidiaGpu(arguments));
    }

    [Theory]
    [InlineData("-i in.mkv -filter:v:0 \"scale_cuda=1280:720\" -c:v libx264 out.ts")]
    [InlineData("-i in.mkv -filter_complex \"[0:v]hwupload=derive_device=cuda[out]\" -c:v libx264 out.ts")]
    [InlineData("-i in.mkv -vf \"hwmap=derive_device=cuda\" -c:v libx264 out.ts")]
    public void UsesNvidiaGpu_DetectsQualifiedAndDerivedCudaFilters(string arguments)
    {
        Assert.True(NvidiaTranscodeDetector.UsesNvidiaGpu(arguments));
    }

    [Theory]
    [InlineData("-init_hw_device cuda:2 -hwaccel cuda -i in.mkv -c:v h264_nvenc out.ts", 2)]
    [InlineData("-init_hw_device cuda=cu:1 -filter_hw_device cu -i in.mkv -c:v h264_nvenc out.ts", 1)]
    [InlineData("-hwaccel cuda -hwaccel_device 3 -i in.mkv -c:v libx264 out.ts", 3)]
    [InlineData("-i in.mkv -c:v hevc_nvenc -gpu 4 out.ts", 4)]
    public void Analyze_ExtractsTheSelectedGpuIndex(string arguments, int expectedGpuIndex)
    {
        var features = NvidiaTranscodeDetector.Analyze(arguments);

        Assert.True(features.UsesGpu);
        Assert.Equal(expectedGpuIndex, features.GpuIndex);
        Assert.False(features.HasConflictingGpuIndices);
    }

    [Fact]
    public void Analyze_FlagsConflictingGpuSelectors()
    {
        var features = NvidiaTranscodeDetector.Analyze(
            "-init_hw_device cuda=cu:0 -hwaccel cuda -hwaccel_device 1 -i in.mkv -c:v h264_nvenc out.ts");

        Assert.True(features.UsesGpu);
        Assert.Null(features.GpuIndex);
        Assert.True(features.HasConflictingGpuIndices);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-hwaccel")]
    public void UsesNvidiaGpu_HandlesEmptyAndTruncatedInput(string? arguments)
    {
        Assert.False(NvidiaTranscodeDetector.UsesNvidiaGpu(arguments));
    }

    [Fact]
    public void Tokenize_KeepsQuotedValuesTogetherAndStripsQuotes()
    {
        var tokens = NvidiaTranscodeDetector.Tokenize("-i \"/media/Some Movie.mkv\" -vf \"a=1,b=2\" -y");

        Assert.Equal(new[] { "-i", "/media/Some Movie.mkv", "-vf", "a=1,b=2", "-y" }, tokens);
    }

    [Fact]
    public void Tokenize_KeepsEmptyQuotedArgument()
    {
        var tokens = NvidiaTranscodeDetector.Tokenize("-metadata \"\" -y");

        Assert.Equal(new[] { "-metadata", string.Empty, "-y" }, tokens);
    }
}
