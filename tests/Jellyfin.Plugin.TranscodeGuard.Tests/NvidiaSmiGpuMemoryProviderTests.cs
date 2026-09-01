using Jellyfin.Plugin.TranscodeGuard.Gpu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

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
    public async Task GetFreeMemoryAsync_ReusesAFailureWithinTheWindow()
    {
        // Failures cannot change a decision - the guard is fail-open either way - so reusing one
        // only spares a queued admission from repeating a slow, doomed lookup.
        using var provider = Create("false", TimeSpan.FromMinutes(5));

        var first = await provider.GetFreeMemoryAsync(0, 5000, CancellationToken.None);
        var second = await provider.GetFreeMemoryAsync(0, 5000, CancellationToken.None);
        var otherGpu = await provider.GetFreeMemoryAsync(1, 5000, CancellationToken.None);

        Assert.False(first.Success);
        Assert.Equal(first.FailureReason, second.FailureReason);
        Assert.False(otherGpu.Success);
    }

    [UnixFact]
    public async Task GetFreeMemoryAsync_NeverServesASuccessfulReadingTwice()
    {
        // A successful reading decides whether a transcode launches. Reusing one would hand two
        // transcodes arriving close together the same pre-allocation number and admit both.
        var script = ScriptedNvidiaSmi("0, 2500");
        try
        {
            using var provider = Create(script, TimeSpan.FromMinutes(5));

            var first = await provider.GetFreeMemoryAsync(0, 5000, CancellationToken.None);
            var second = await provider.GetFreeMemoryAsync(0, 5000, CancellationToken.None);

            Assert.True(first.Success);
            Assert.Equal(2500, first.FreeMiB);
            Assert.True(second.Success);

            // Two invocations of the stand-in binary, i.e. two real lookups.
            Assert.Equal(2, File.ReadAllLines(script + ".calls").Length);
        }
        finally
        {
            File.Delete(script);
            File.Delete(script + ".calls");
        }
    }

    [UnixFact]
    public async Task GetFreeMemoryAsync_DoesNotCacheACancelledRequestAsAGpuFailure()
    {
        // A cancelled streaming request says nothing about the GPU. If its result were cached,
        // the next admission would be handed a failure, fail open, and launch without ever
        // reading VRAM. The long failure window makes that leak visible if it regresses.
        var script = ScriptedNvidiaSmi("0, 2500", sleepSeconds: 1);
        try
        {
            using var provider = Create(script, TimeSpan.FromMinutes(5));

            using var cancelled = new CancellationTokenSource();
            var inFlight = provider.GetFreeMemoryAsync(0, 30000, cancelled.Token);
            await Task.Delay(150);
            await cancelled.CancelAsync();

            var first = await inFlight;
            Assert.False(first.Success);

            // A different request must still get a real lookup.
            var second = await provider.GetFreeMemoryAsync(0, 30000, CancellationToken.None);

            Assert.True(second.Success);
            Assert.Equal(2500, second.FreeMiB);
            Assert.Equal(2, File.ReadAllLines(script + ".calls").Length);
        }
        finally
        {
            File.Delete(script);
            File.Delete(script + ".calls");
        }
    }

    /// <summary>
    /// Writes an executable stand-in for nvidia-smi that prints fixed csv output and records
    /// each invocation, so "was this a real lookup?" can be asserted.
    /// </summary>
    /// <param name="csvOutput">The line to emit on stdout.</param>
    /// <param name="sleepSeconds">How long to stall before answering, so a query can be cancelled mid-flight.</param>
    /// <returns>Path to the stand-in executable.</returns>
    private static string ScriptedNvidiaSmi(string csvOutput, int sleepSeconds = 0)
    {
        var path = Path.Combine(Path.GetTempPath(), "fake-nvidia-smi-" + Guid.NewGuid().ToString("N") + ".sh");
        var stall = sleepSeconds > 0 ? $"sleep {sleepSeconds}\n" : string.Empty;
        File.WriteAllText(path, $"#!/bin/sh\necho called >> \"{path}.calls\"\n{stall}echo '{csvOutput}'\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    [Fact]
    public async Task GetFreeMemoryAsync_ConcurrentCallsDoNotDeadlock()
    {
        using var provider = Create("true", TimeSpan.FromSeconds(1));

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => provider.GetFreeMemoryAsync(0, 5000, CancellationToken.None)));

        Assert.All(results, result => Assert.False(result.Success));
    }

    [UnixFact]
    public async Task GetUsedMemoryAsync_AttributesTheRequestedFfmpegProcess()
    {
        var script = ScriptedNvidiaSmi("2378147, 1339");
        try
        {
            using var provider = Create(script);

            var result = await ((IGpuProcessMemoryProvider)provider).GetUsedMemoryAsync(
                2378147,
                5000,
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(1339, result.UsedMiB);
        }
        finally
        {
            File.Delete(script);
            File.Delete(script + ".calls");
        }
    }
}
