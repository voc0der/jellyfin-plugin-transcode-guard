namespace Jellyfin.Plugin.TranscodeGuard.Gpu;

/// <summary>
/// Outcome of a single free-VRAM lookup.
/// </summary>
public readonly struct GpuMemoryQueryResult
{
    private GpuMemoryQueryResult(bool success, int freeMiB, string? failureReason)
    {
        Success = success;
        FreeMiB = freeMiB;
        FailureReason = failureReason;
    }

    /// <summary>
    /// Gets a value indicating whether free VRAM could be determined.
    /// </summary>
    public bool Success { get; }

    /// <summary>
    /// Gets the free VRAM in MiB. Only meaningful when <see cref="Success"/> is true.
    /// </summary>
    public int FreeMiB { get; }

    /// <summary>
    /// Gets a short, log-safe description of why the query failed.
    /// </summary>
    public string? FailureReason { get; }

    public static GpuMemoryQueryResult FromFreeMiB(int freeMiB) => new(true, freeMiB, null);

    public static GpuMemoryQueryResult Failed(string reason) => new(false, 0, reason);
}
