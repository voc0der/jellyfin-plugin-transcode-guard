using Jellyfin.Plugin.TranscodeNag.Gpu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.TranscodeNag.Tests;

/// <summary>
/// Exercises the real process path against stand-in executables, so the fail-open behaviour is
/// verified rather than assumed.
/// </summary>
public class NvidiaSmiGpuMemoryProviderTests
{
    private static NvidiaSmiGpuMemoryProvider Create(string executablePath, TimeSpan? cacheWindow = null)
    {
        return new NvidiaSmiGpuMemoryProvider(
            NullLogger<NvidiaSmiGpuMemoryProvider>.Instance,
            () => executablePath,
            cacheWindow ?? TimeSpan.Zero);
    }

    [Fact]
    public async Task GetFreeMemoryAsync_MissingExecutableFailsWithoutThrowing()
    {
        using var provider = Create(Path.Combine(Path.GetTempPath(), "definitely-not-nvidia-smi-" + Guid.NewGuid().ToString("N")));

        var result = await provider.GetFreeMemoryAsync(0, 1000, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [Fact]
    public async Task GetFreeMemoryAsync_NonZeroExitFailsWithoutThrowing()
    {
        using var provider = Create("false");

        var result = await provider.GetFreeMemoryAsync(0, 5000, CancellationToken.None);

        // Fail-open holds on every platform. The exit-code wording only applies where the
        // stand-in "false" binary actually exists.
        Assert.False(result.Success);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains("exited with code", result.FailureReason);
        }
    }

    [Fact]
    public async Task GetFreeMemoryAsync_UnparseableOutputFails()
    {
        // "true" exits 0 with no output at all - the closest stand-in for a driver that answers
        // successfully but tells us nothing usable.
        using var provider = Create("true");

        var result = await provider.GetFreeMemoryAsync(0, 5000, CancellationToken.None);

        Assert.False(result.Success);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains("did not report a usable value", result.FailureReason);
        }
    }

    [Fact]
    public async Task GetFreeMemoryAsync_AlreadyCancelledRequestFailsOpenRatherThanThrowing()
    {
        using var provider = Create("true");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await provider.GetFreeMemoryAsync(0, 5000, cancelled.Token);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task GetFreeMemoryAsync_CachesWithinTheWindowAndReQueriesForAnotherGpu()
    {
        using var provider = Create("false", TimeSpan.FromMinutes(5));

        var first = await provider.GetFreeMemoryAsync(0, 5000, CancellationToken.None);
        var second = await provider.GetFreeMemoryAsync(0, 5000, CancellationToken.None);
        var otherGpu = await provider.GetFreeMemoryAsync(1, 5000, CancellationToken.None);

        Assert.False(first.Success);
        Assert.Equal(first.FailureReason, second.FailureReason);
        Assert.False(otherGpu.Success);
    }

    [Fact]
    public async Task GetFreeMemoryAsync_ConcurrentCallsDoNotDeadlock()
    {
        using var provider = Create("true", TimeSpan.FromSeconds(1));

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => provider.GetFreeMemoryAsync(0, 5000, CancellationToken.None)));

        Assert.All(results, result => Assert.False(result.Success));
    }
}
