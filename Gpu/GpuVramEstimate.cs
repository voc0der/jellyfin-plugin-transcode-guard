namespace Jellyfin.Plugin.TranscodeNag.Gpu;

/// <summary>
/// A conservative, quarter-GiB budget for the video memory one FFmpeg job will allocate.
/// </summary>
internal readonly record struct GpuVramEstimate(
    int BudgetMiB,
    int SourceWidth,
    int SourceHeight,
    int SourceBitDepth,
    int OutputWidth,
    int OutputHeight,
    int OutputBitDepth,
    bool UsesTonemap,
    bool UsedFallbackMetadata)
{
    /// <summary>
    /// Gets the zero budget for a request that allocates no NVIDIA video memory.
    /// </summary>
    internal static GpuVramEstimate Zero => default;
}
