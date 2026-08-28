using System;
using Jellyfin.Plugin.TranscodeNag.Configuration;

namespace Jellyfin.Plugin.TranscodeNag.Gpu;

/// <summary>
/// Why a transcode was allowed, or that it was refused.
/// </summary>
internal enum GpuAdmissionOutcome
{
    /// <summary>The guard is switched off.</summary>
    AllowedGuardDisabled,

    /// <summary>Direct Play, remux/stream copy, or audio-only - no GPU video encode involved.</summary>
    AllowedNotGpuTranscode,

    /// <summary>The pending job's conservative VRAM requirement fits in the currently free memory.</summary>
    AllowedSufficientMemory,

    /// <summary>Free VRAM could not be determined; the guard is fail-open.</summary>
    AllowedQueryFailed,

    /// <summary>The pending job's VRAM budget does not fit in the currently free memory.</summary>
    Denied
}

/// <summary>
/// The admission rules, kept free of Jellyfin and process types so they can be tested directly.
/// </summary>
internal static class GpuAdmissionPolicy
{
    /// <summary>
    /// Returns true when Jellyfin's finished job description means a GPU-backed video encode.
    /// </summary>
    /// <param name="isVideoRequest">Whether the streaming request asked for video at all.</param>
    /// <param name="outputVideoCodec">Jellyfin's chosen output video codec ("copy" for remux).</param>
    /// <param name="commandLineArguments">The FFmpeg arguments Jellyfin built for this job.</param>
    /// <returns>True when the job needs NVIDIA GPU memory.</returns>
    internal static bool RequiresGpuVideoTranscode(
        bool isVideoRequest,
        string? outputVideoCodec,
        string? commandLineArguments)
    {
        // Audio-only requests never allocate video memory.
        if (!isVideoRequest)
        {
            return false;
        }

        // Stream copy: container remux or Direct Stream. The video is passed through untouched.
        if (IsCopyCodec(outputVideoCodec))
        {
            return false;
        }

        return NvidiaTranscodeDetector.UsesNvidiaGpu(commandLineArguments);
    }

    /// <summary>
    /// Checks whether the pending job and any admitted-but-not-yet-visible jobs fit in free VRAM.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="requiresGpuVideoTranscode">Result of <see cref="RequiresGpuVideoTranscode"/>.</param>
    /// <param name="memory">The free-VRAM reading, or null when the guard short-circuited before querying.</param>
    /// <returns>The admission outcome.</returns>
    internal static GpuAdmissionOutcome Evaluate(
        PluginConfiguration config,
        bool requiresGpuVideoTranscode,
        GpuMemoryQueryResult? memory,
        int jobBudgetMiB,
        int inFlightBudgetMiB = 0)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!config.EnableGpuResourceGuard)
        {
            return GpuAdmissionOutcome.AllowedGuardDisabled;
        }

        if (!requiresGpuVideoTranscode)
        {
            return GpuAdmissionOutcome.AllowedNotGpuTranscode;
        }

        // Fail open: an unknown GPU state must never turn into a blanket denial.
        if (memory is not { Success: true } reading)
        {
            return GpuAdmissionOutcome.AllowedQueryFailed;
        }

        var requiredMiB = RequiredMemoryMiB(jobBudgetMiB, inFlightBudgetMiB);

        return reading.FreeMiB >= requiredMiB
            ? GpuAdmissionOutcome.AllowedSufficientMemory
            : GpuAdmissionOutcome.Denied;
    }

    /// <summary>
    /// Gets the free VRAM required to admit a job, including pending jobs.
    /// Each individual budget already contains margin and is rounded up to 256 MiB.
    /// </summary>
    internal static int RequiredMemoryMiB(int jobBudgetMiB, int inFlightBudgetMiB = 0)
    {
        var required = (long)Math.Max(0, jobBudgetMiB)
            + Math.Max(0, inFlightBudgetMiB);

        return (int)Math.Min(int.MaxValue, required);
    }

    /// <summary>
    /// Returns true when the guard must read GPU state before deciding.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="requiresGpuVideoTranscode">Result of <see cref="RequiresGpuVideoTranscode"/>.</param>
    /// <returns>True when a GPU query is needed.</returns>
    internal static bool RequiresGpuQuery(PluginConfiguration config, bool requiresGpuVideoTranscode)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.EnableGpuResourceGuard && requiresGpuVideoTranscode;
    }

    internal static bool IsCopyCodec(string? codec)
        => string.Equals(codec, "copy", StringComparison.OrdinalIgnoreCase);
}
