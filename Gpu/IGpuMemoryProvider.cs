using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TranscodeNag.Gpu;

/// <summary>
/// Reads free video memory for a single GPU.
/// </summary>
public interface IGpuMemoryProvider
{
    /// <summary>
    /// Gets the free VRAM for <paramref name="gpuIndex"/>.
    /// Implementations never throw for an unavailable GPU, driver, or tool; they return a failed result instead.
    /// </summary>
    /// <param name="gpuIndex">Zero-based GPU index.</param>
    /// <param name="timeoutMilliseconds">Upper bound on how long the lookup may take.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The free memory, or a failure describing why it is unknown.</returns>
    Task<GpuMemoryQueryResult> GetFreeMemoryAsync(int gpuIndex, int timeoutMilliseconds, CancellationToken cancellationToken);
}
