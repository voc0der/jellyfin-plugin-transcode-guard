using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.TranscodeGuard.Gpu;

/// <summary>
/// Decides whether an FFmpeg invocation Jellyfin is about to launch will touch the NVIDIA GPU.
/// </summary>
/// <remarks>
/// This reads Jellyfin's own finished command line rather than re-deriving codec compatibility.
/// If Jellyfin decided on CUDA/NVENC, the flags are already there; if it chose CPU, DirectPlay,
/// or a stream copy, they are not.
/// </remarks>
internal static class NvidiaTranscodeDetector
{
    private static readonly string[] VideoCodecFlagPrefixes = { "-c:v", "-codec:v", "-vcodec" };

    private static readonly string[] FilterFlags = { "-vf", "-filter:v", "-filter_complex", "-lavfi" };

    /// <summary>
    /// Returns true when the command line uses CUDA/NVENC/NVDEC in any role: hardware decode,
    /// hardware filtering, or hardware encode. All three hold VRAM.
    /// </summary>
    /// <param name="commandLineArguments">The FFmpeg arguments Jellyfin built for this job.</param>
    /// <returns>True when the job needs NVIDIA GPU memory.</returns>
    internal static bool UsesNvidiaGpu(string? commandLineArguments)
        => Analyze(commandLineArguments).UsesGpu;

    /// <summary>
    /// Describes which parts of an FFmpeg job allocate NVIDIA video memory.
    /// </summary>
    /// <param name="commandLineArguments">The FFmpeg arguments Jellyfin built for this job.</param>
    /// <returns>The NVIDIA features present in the command line.</returns>
    internal static NvidiaTranscodeFeatures Analyze(string? commandLineArguments)
    {
        if (string.IsNullOrWhiteSpace(commandLineArguments))
        {
            return default;
        }

        var tokens = Tokenize(commandLineArguments);
        var usesGpu = false;
        var usesDecoder = false;
        var usesEncoder = false;
        var usesFilters = false;
        var usesTonemap = false;
        var usesScaling = false;
        var usesOtherFilters = false;
        int? gpuIndex = null;
        var hasConflictingGpuIndices = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var flag = tokens[i];
            if (flag.Length == 0 || flag[0] != '-')
            {
                continue;
            }

            // Every marker below is a flag/value pair, so a trailing flag can never match.
            if (i + 1 >= tokens.Count)
            {
                break;
            }

            var value = tokens[i + 1];

            // -hwaccel cuda / -hwaccel nvdec / -hwaccel_output_format cuda
            if ((Is(flag, "-hwaccel") || Is(flag, "-hwaccel_output_format"))
                && (Is(value, "cuda") || Is(value, "nvdec")))
            {
                usesGpu = true;
                usesDecoder = true;
                continue;
            }

            // -init_hw_device cuda=cu:0
            if (Is(flag, "-init_hw_device") && StartsWithDeviceType(value, "cuda"))
            {
                usesGpu = true;
                RecordGpuIndex(
                    ParseInitDeviceIndex(value),
                    ref gpuIndex,
                    ref hasConflictingGpuIndices);
                continue;
            }

            if (Is(flag, "-hwaccel_device") || Is(flag, "-gpu"))
            {
                RecordGpuIndex(
                    ParseInteger(value),
                    ref gpuIndex,
                    ref hasConflictingGpuIndices);
                continue;
            }

            // -codec:v:0 av1_nvenc, -c:v h264_cuvid
            if (IsVideoCodecFlag(flag)
                && (EndsWith(value, "_nvenc") || EndsWith(value, "_cuvid")))
            {
                usesGpu = true;
                usesEncoder |= EndsWith(value, "_nvenc");
                usesDecoder |= EndsWith(value, "_cuvid");
                continue;
            }

            // Parse filter identifiers rather than searching the whole graph. Subtitle filters
            // embed file paths, and a path such as show_cuda.srt is not a CUDA filter.
            if (IsFilterFlag(flag))
            {
                var filterFeatures = AnalyzeFilterGraph(value);
                usesGpu |= filterFeatures.UsesGpu;
                usesFilters |= filterFeatures.UsesGpu;
                usesTonemap |= filterFeatures.UsesTonemap;
                usesScaling |= filterFeatures.UsesScaling;
                usesOtherFilters |= filterFeatures.UsesOtherFilters;
            }
        }

        return new NvidiaTranscodeFeatures(
            usesGpu,
            usesDecoder,
            usesEncoder,
            usesFilters,
            usesTonemap,
            usesScaling,
            usesOtherFilters,
            hasConflictingGpuIndices ? null : gpuIndex,
            hasConflictingGpuIndices);
    }

    /// <summary>
    /// Splits an FFmpeg argument string on whitespace, honouring double-quoted values
    /// (Jellyfin quotes media paths and filter graphs that contain spaces).
    /// </summary>
    /// <param name="commandLine">The raw argument string.</param>
    /// <returns>The unquoted tokens.</returns>
    internal static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static bool IsVideoCodecFlag(string flag)
    {
        // Matches "-c:v" as well as the stream-qualified "-codec:v:0".
        return VideoCodecFlagPrefixes.Any(prefix =>
            flag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (flag.Length == prefix.Length || flag[prefix.Length] == ':'));
    }

    private static bool IsFilterFlag(string flag)
        => FilterFlags.Any(filterFlag =>
            flag.StartsWith(filterFlag, StringComparison.OrdinalIgnoreCase)
            && (flag.Length == filterFlag.Length || flag[filterFlag.Length] == ':'));

    private static bool StartsWithDeviceType(string value, string deviceType)
    {
        // "cuda" alone, or "cuda=cu:0".
        return Is(value, deviceType)
            || value.StartsWith(deviceType + "=", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(deviceType + ":", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ParseInitDeviceIndex(string value)
    {
        var colon = value.IndexOf(':');
        if (colon < 0 || colon == value.Length - 1)
        {
            return null;
        }

        var device = value[(colon + 1)..];
        var comma = device.IndexOf(',');
        if (comma >= 0)
        {
            device = device[..comma];
        }

        return ParseInteger(device);
    }

    private static int? ParseInteger(string value)
        => int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 0
                ? parsed
                : null;

    private static void RecordGpuIndex(int? candidate, ref int? selected, ref bool conflicting)
    {
        if (!candidate.HasValue)
        {
            return;
        }

        if (selected.HasValue && selected.Value != candidate.Value)
        {
            conflicting = true;
            return;
        }

        selected = candidate;
    }

    private static NvidiaFilterFeatures AnalyzeFilterGraph(string graph)
    {
        var usesGpu = false;
        var usesTonemap = false;
        var usesScaling = false;
        var usesOtherFilters = false;
        var segmentStart = 0;
        var escaped = false;
        var inSingleQuotes = false;
        var inDoubleQuotes = false;

        for (var i = 0; i <= graph.Length; i++)
        {
            var atEnd = i == graph.Length;
            var c = atEnd ? '\0' : graph[i];

            if (!atEnd && escaped)
            {
                escaped = false;
                continue;
            }

            if (!atEnd && c == '\\')
            {
                escaped = true;
                continue;
            }

            if (!atEnd && c == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
                continue;
            }

            if (!atEnd && c == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
                continue;
            }

            if (!atEnd && (inSingleQuotes || inDoubleQuotes || (c != ',' && c != ';')))
            {
                continue;
            }

            var segment = graph[segmentStart..i].TrimStart();
            segmentStart = i + 1;

            while (segment.StartsWith("[", StringComparison.Ordinal))
            {
                var closingLabel = segment.IndexOf(']');
                if (closingLabel < 0)
                {
                    segment = string.Empty;
                    break;
                }

                segment = segment[(closingLabel + 1)..].TrimStart();
            }

            var nameLength = 0;
            while (nameLength < segment.Length
                && (char.IsLetterOrDigit(segment[nameLength]) || segment[nameLength] == '_'))
            {
                nameLength++;
            }

            if (nameLength == 0)
            {
                continue;
            }

            var name = segment[..nameLength];
            var isCudaFilter = name.EndsWith("_cuda", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("_npp", StringComparison.OrdinalIgnoreCase)
                || ((Is(name, "hwupload") || Is(name, "hwmap"))
                    && Contains(segment, "derive_device=cuda"));

            if (!isCudaFilter)
            {
                continue;
            }

            usesGpu = true;
            usesTonemap |= Is(name, "tonemap_cuda");
            usesScaling |= Is(name, "scale_cuda") || Is(name, "scale_npp");
            usesOtherFilters |= !Is(name, "tonemap_cuda")
                && !Is(name, "scale_cuda")
                && !Is(name, "scale_npp")
                && !Is(name, "hwupload_cuda")
                && !Is(name, "hwupload")
                && !Is(name, "hwmap");
        }

        return new NvidiaFilterFeatures(usesGpu, usesTonemap, usesScaling, usesOtherFilters);
    }

    private static bool Is(string value, string expected)
        => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static bool EndsWith(string value, string suffix)
        => value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string value, string fragment)
        => value.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// NVIDIA-backed stages found in an FFmpeg command line.
/// </summary>
/// <param name="UsesGpu">Whether any NVIDIA stage is present.</param>
/// <param name="UsesDecoder">Whether decoded frames live on the GPU.</param>
/// <param name="UsesEncoder">Whether NVENC is used.</param>
/// <param name="UsesFilters">Whether a CUDA/NPP filter graph is used.</param>
/// <param name="UsesTonemap">Whether CUDA tone mapping is used.</param>
/// <param name="UsesScaling">Whether CUDA/NPP scaling is used.</param>
/// <param name="UsesOtherFilters">Whether additional CUDA/NPP filters need intermediate surfaces.</param>
/// <param name="GpuIndex">The explicitly selected numeric GPU, when unambiguous.</param>
/// <param name="HasConflictingGpuIndices">Whether the command contains contradictory GPU selectors.</param>
internal readonly record struct NvidiaTranscodeFeatures(
    bool UsesGpu,
    bool UsesDecoder,
    bool UsesEncoder,
    bool UsesFilters,
    bool UsesTonemap,
    bool UsesScaling,
    bool UsesOtherFilters,
    int? GpuIndex,
    bool HasConflictingGpuIndices);

internal readonly record struct NvidiaFilterFeatures(
    bool UsesGpu,
    bool UsesTonemap,
    bool UsesScaling,
    bool UsesOtherFilters);
