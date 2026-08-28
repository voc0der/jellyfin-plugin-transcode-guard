using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TranscodeNag.Gpu;

/// <summary>
/// Optionally attributes NVIDIA memory to a launched process for calibration and reservation
/// reconciliation. Admission does not depend on this optional query succeeding.
/// </summary>
internal interface IGpuProcessMemoryProvider
{
    Task<GpuProcessMemoryQueryResult> GetUsedMemoryAsync(
        int processId,
        int timeoutMilliseconds,
        CancellationToken cancellationToken);
}
