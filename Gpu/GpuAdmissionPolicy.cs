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
    /// Minimum and maximum values accepted for <see cref="PluginConfiguration.GpuVramBudgetPercent"/>.
    /// A stored configuration is not necessarily one this build's settings page wrote, so the
    /// bounds are enforced here rather than trusted from the UI.
    /// </summary>
    internal const int MinimumBudgetPercent = 10;

    /// <summary>The largest accepted VRAM budget percentage.</summary>
    internal const int MaximumBudgetPercent = 500;

    /// <summary>
    /// Applies the admin's budget percentage to one job's automatic requirement.
    /// </summary>
    /// <remarks>
    /// The model produces the worst plausible peak for a job shape. An admin who has watched the
    /// calibration log on their own hardware knows better than the model does, so this scales the
    /// requirement rather than forcing them to choose between the model's number and no guard at
    /// all. A job that needs any VRAM keeps needing at least 1 MiB, so scaling can never turn a
    /// real transcode into a free one.
    /// </remarks>
    /// <param name="budgetMiB">The model's conservative requirement.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>The requirement the guard will actually demand.</returns>
    internal static int ScaleBudgetMiB(int budgetMiB, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (budgetMiB <= 0)
        {
            return 0;
        }

        var percent = EffectiveBudgetPercent(config);
        if (percent == 100)
        {
            return budgetMiB;
        }

        var scaled = (long)budgetMiB * percent / 100;

        return (int)Math.Clamp(scaled, 1, int.MaxValue);
    }

    /// <summary>
    /// Gets the budget percentage actually in force, with an out-of-range stored value clamped.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>The percentage applied to every model budget.</returns>
    internal static int EffectiveBudgetPercent(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return Math.Clamp(config.GpuVramBudgetPercent, MinimumBudgetPercent, MaximumBudgetPercent);
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
