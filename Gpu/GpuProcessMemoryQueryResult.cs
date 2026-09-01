namespace Jellyfin.Plugin.TranscodeGuard.Gpu;

/// <summary>
/// Outcome of attributing NVIDIA memory to one operating-system process.
/// </summary>
internal readonly record struct GpuProcessMemoryQueryResult(
    bool Success,
    int UsedMiB,
    string? FailureReason)
{
    internal static GpuProcessMemoryQueryResult FromUsedMiB(int usedMiB)
        => new(true, usedMiB, null);

    internal static GpuProcessMemoryQueryResult Failed(string reason)
        => new(false, 0, reason);
}
