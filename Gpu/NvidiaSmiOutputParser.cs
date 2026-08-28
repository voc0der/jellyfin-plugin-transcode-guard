using System;
using System.Globalization;

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

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(',', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var indexText = line.AsSpan(0, separator).Trim();
            var freeText = line.AsSpan(separator + 1).Trim();

            // A second comma would mean the caller changed the query; only the first two fields are ours.
            var extraSeparator = freeText.IndexOf(',');
            if (extraSeparator >= 0)
            {
                freeText = freeText[..extraSeparator].Trim();
            }

            if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIndex)
                || parsedIndex != gpuIndex)
            {
                continue;
            }

            if (!int.TryParse(freeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFree)
                || parsedFree < 0)
            {
                // The index matched but the value is unusable (for example "[N/A]" while the driver reloads).
                return false;
            }

            freeMiB = parsedFree;
            return true;
        }

        return false;
    }
}
