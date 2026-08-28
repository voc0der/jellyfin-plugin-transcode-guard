using System;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.TranscodeNag.Gpu;

/// <summary>
/// Parses the machine-readable nvidia-smi output the guard asks for:
/// <c>--query-gpu=index,memory.free --format=csv,noheader,nounits</c>.
/// Kept separate from process handling so it can be unit tested directly.
/// </summary>
internal static class NvidiaSmiOutputParser
{
    /// <summary>
    /// Finds the free-memory value for <paramref name="gpuIndex"/> in a csv,noheader,nounits payload.
    /// </summary>
    /// <param name="output">Raw stdout from nvidia-smi.</param>
    /// <param name="gpuIndex">Zero-based GPU index to look for.</param>
    /// <param name="freeMiB">Parsed free memory in MiB.</param>
    /// <returns>True when the requested index was present and parsed.</returns>
    internal static bool TryGetFreeMiB(string? output, int gpuIndex, out int freeMiB)
    {
        freeMiB = 0;

        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var rows = output
            .Split('\n')
            .Select(rawLine => rawLine.Trim())
            .Where(line => line.Contains(',', StringComparison.Ordinal));

        foreach (var row in rows)
        {
            var separator = row.IndexOf(',', StringComparison.Ordinal);

            if (!int.TryParse(
                    row.AsSpan(0, separator).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedIndex)
                || parsedIndex != gpuIndex)
            {
                continue;
            }

            // The index matched, so this is our row. A value we cannot read (for example "[N/A]"
            // while the driver reloads) means the lookup failed, not that we keep scanning.
            return TryParseFreeMiB(row.AsSpan(separator + 1), out freeMiB);
        }

        return false;
    }

    /// <summary>
    /// Sums the used-memory rows attributed to one process. A process can appear once per GPU.
    /// </summary>
    internal static bool TryGetProcessUsedMiB(string? output, int processId, out int usedMiB)
    {
        usedMiB = 0;
        if (processId <= 0 || string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        long totalMiB = 0;
        var found = false;
        var rows = output
            .Split('\n')
            .Select(rawLine => rawLine.Trim())
            .Where(line => line.Contains(',', StringComparison.Ordinal));

        foreach (var row in rows)
        {
            var separator = row.IndexOf(',', StringComparison.Ordinal);
            if (!int.TryParse(
                    row.AsSpan(0, separator).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedProcessId)
                || parsedProcessId != processId)
            {
                continue;
            }

            if (!TryParseFreeMiB(row.AsSpan(separator + 1), out var processMiB))
            {
                return false;
            }

            found = true;
            totalMiB += processMiB;
        }

        if (!found)
        {
            return false;
        }

        usedMiB = (int)Math.Min(int.MaxValue, totalMiB);
        return true;
    }

    private static bool TryParseFreeMiB(ReadOnlySpan<char> value, out int freeMiB)
    {
        freeMiB = 0;

        var trimmed = value.Trim();

        // A second comma would mean the caller changed the query; only the first two fields are ours.
        var extraSeparator = trimmed.IndexOf(',');
        if (extraSeparator >= 0)
        {
            trimmed = trimmed[..extraSeparator].Trim();
        }

        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0)
        {
            return false;
        }

        freeMiB = parsed;
        return true;
    }
}
