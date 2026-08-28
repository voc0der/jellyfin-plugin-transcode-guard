using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TranscodeNag.Gpu;

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
    {
        if (string.IsNullOrWhiteSpace(commandLineArguments))
        {
            return false;
        }

        var tokens = Tokenize(commandLineArguments);

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
                return true;
            }

            // -init_hw_device cuda=cu:0
            if (Is(flag, "-init_hw_device") && StartsWithDeviceType(value, "cuda"))
            {
                return true;
            }

            // -codec:v:0 av1_nvenc, -c:v h264_cuvid
            if (IsVideoCodecFlag(flag)
                && (EndsWith(value, "_nvenc") || EndsWith(value, "_cuvid")))
            {
                return true;
            }

            // -vf "tonemap_cuda=...,scale_cuda=..." - filter graphs never contain file paths,
            // so a substring match here cannot be tripped by a media file name.
            if (IsFilterFlag(flag)
                && (Contains(value, "_cuda") || Contains(value, "_npp") || Contains(value, "cuda=")))
            {
                return true;
            }
        }

        return false;
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
        foreach (var prefix in VideoCodecFlagPrefixes)
        {
            // Matches "-c:v" as well as the stream-qualified "-codec:v:0".
            if (flag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (flag.Length == prefix.Length || flag[prefix.Length] == ':'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFilterFlag(string flag)
    {
        foreach (var filterFlag in FilterFlags)
        {
            if (Is(flag, filterFlag))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWithDeviceType(string value, string deviceType)
    {
        // "cuda" alone, or "cuda=cu:0".
        return Is(value, deviceType)
            || value.StartsWith(deviceType + "=", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Is(string value, string expected)
        => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static bool EndsWith(string value, string suffix)
        => value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string value, string fragment)
        => value.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
