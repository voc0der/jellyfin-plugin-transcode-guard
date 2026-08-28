using Jellyfin.Plugin.TranscodeNag.Gpu;

namespace Jellyfin.Plugin.TranscodeNag.Tests;

public class NvidiaSmiOutputParserTests
{
    private const string TwoGpuOutput = "0, 2318\n1, 20044\n";

    [Fact]
    public void TryGetFreeMiB_ReadsTheRequestedGpu()
    {
        Assert.True(NvidiaSmiOutputParser.TryGetFreeMiB(TwoGpuOutput, 0, out var gpu0));
        Assert.Equal(2318, gpu0);

        Assert.True(NvidiaSmiOutputParser.TryGetFreeMiB(TwoGpuOutput, 1, out var gpu1));
        Assert.Equal(20044, gpu1);
    }

    [Fact]
    public void TryGetFreeMiB_ToleratesCarriageReturnsAndPadding()
    {
        Assert.True(NvidiaSmiOutputParser.TryGetFreeMiB("  0 ,  2318  \r\n", 0, out var free));
        Assert.Equal(2318, free);
    }

    [Fact]
    public void TryGetFreeMiB_ReturnsFalseForAnAbsentGpuIndex()
    {
        Assert.False(NvidiaSmiOutputParser.TryGetFreeMiB(TwoGpuOutput, 3, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n")]
    [InlineData("Failed to initialize NVML: Driver/library version mismatch")]
    [InlineData("0, [N/A]")]
    [InlineData("0, -5")]
    [InlineData("not-a-number, 2318")]
    public void TryGetFreeMiB_ReturnsFalseForUnusableOutput(string? output)
    {
        Assert.False(NvidiaSmiOutputParser.TryGetFreeMiB(output, 0, out var free));
        Assert.Equal(0, free);
    }

    [Fact]
    public void TryGetFreeMiB_IgnoresExtraTrailingColumns()
    {
        // Guards against a future query string picking up more fields than the parser expects.
        Assert.True(NvidiaSmiOutputParser.TryGetFreeMiB("0, 2318, 24576", 0, out var free));
        Assert.Equal(2318, free);
    }

    [Fact]
    public void TryGetProcessUsedMiB_ReadsAndSumsTheRequestedProcess()
    {
        const string Output = "2378147, 1339\n3230507, 160\n2378147, 12\n";

        Assert.True(NvidiaSmiOutputParser.TryGetProcessUsedMiB(Output, 2378147, out var usedMiB));
        Assert.Equal(1351, usedMiB);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2378147, [N/A]")]
    [InlineData("2378147, -1")]
    [InlineData("other, 1339")]
    public void TryGetProcessUsedMiB_ReturnsFalseForUnusableOutput(string? output)
    {
        Assert.False(NvidiaSmiOutputParser.TryGetProcessUsedMiB(output, 2378147, out var usedMiB));
        Assert.Equal(0, usedMiB);
    }
}
