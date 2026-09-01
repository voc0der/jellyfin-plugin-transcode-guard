using System;
using System.Linq;

namespace Jellyfin.Plugin.TranscodeGuard.Gpu;

/// <summary>
/// Assigns a conservative NVIDIA video-memory requirement to one FFmpeg transcode.
/// </summary>
/// <remarks>
/// The bands come from nvidia-smi process rows on an RTX 4000 Ada: about 390 MiB for a small show
/// transcode, 824 MiB for a 4K Main10 HDR transcode without tone mapping, and 1339-1381 MiB for a
/// 4K HDR job tone mapped to AV1.
/// <para>
/// A band is a measurement plus a stated margin, and the margin is the part worth arguing with.
/// It was previously invisible: 824 MiB of measurement was banded to 1024, which reads like
/// rounding but is a 24% markup on a number that decides whether playback happens. Compounded with
/// the tone-map band it refused a 4K HDR job budgeted at 1536 MiB on a card with 1490 MiB free -
/// a job later measured, running, at 1381 MiB. A budget above what the job provably uses is not
/// conservatism, it is a wrong answer that happens to fail safe.
/// </para>
/// </remarks>
internal static class GpuVramEstimator
{
    private const int DefaultWidth = 3840;
    private const int DefaultHeight = 2160;
    private const int DefaultBitDepth = 10;
    private const long FullHdPixels = 1920L * 1080;
    private const long QuadHdPixels = 2560L * 1440;
    private const long UltraHdPixels = 3840L * 2160;

    // 824 MiB measured for 4K Main10 HDR without tone mapping, plus roughly 9% margin. The band it
    // replaces was 1024, which put a tone-mapped 4K job at 1536 MiB - 155 MiB above the 1381 MiB
    // that same job was measured using while it played.
    private const int UltraHd10BitBudgetMiB = 896;

    // The measured tone-map delta is 1381 - 824 = 557 MiB. This band is deliberately *under* that:
    // the 10-bit base above already carries the margin for the whole pipeline, and stacking a
    // padded surcharge on a padded base is what produced 1536.
    private const int UltraHdTonemapSurchargeMiB = 512;

    // No direct measurement for 4K 8-bit. It is bounded above by the 10-bit figure, so it keeps the
    // band it had rather than inventing a number.
    private const int UltraHd8BitBudgetMiB = 768;

    // Unknown source metadata is charged the heaviest shape that has actually been measured - 4K
    // 10-bit with tone mapping - and no more. Charging an unmeasured job above every measured one
    // is how the padding got in.
    private const int UnknownSourceFloorMiB = UltraHd10BitBudgetMiB + UltraHdTonemapSurchargeMiB;

    /// <summary>
    /// Budgets VRAM for a job from Jellyfin's completed transcode description.
    /// </summary>
    /// <param name="request">The pending transcode.</param>
    /// <returns>The conservative budget and normalized job shape.</returns>
    internal static GpuVramEstimate Estimate(GpuTranscodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.IsVideoRequest || GpuAdmissionPolicy.IsCopyCodec(request.OutputVideoCodec))
        {
            return GpuVramEstimate.Zero;
        }

        var features = NvidiaTranscodeDetector.Analyze(request.CommandLineArguments);
        if (!features.UsesGpu)
        {
            return GpuVramEstimate.Zero;
        }

        var inferredSourceBitDepth = InferPixelFormatBitDepth(request.SourcePixelFormat);
        var inferredOutputBitDepth = InferOutputBitDepth(request.CommandLineArguments);
        var usedSourceFallback = !IsDimension(request.SourceWidth)
            || !IsDimension(request.SourceHeight)
            || (!IsBitDepth(request.SourceBitDepth) && !inferredSourceBitDepth.HasValue);
        var usedFallback = usedSourceFallback
            || !IsDimension(request.OutputWidth)
            || !IsDimension(request.OutputHeight);

        var sourceWidth = ValidDimension(request.SourceWidth, DefaultWidth);
        var sourceHeight = ValidDimension(request.SourceHeight, DefaultHeight);
        var sourceBitDepth = ValidBitDepth(request.SourceBitDepth, inferredSourceBitDepth ?? DefaultBitDepth);
        var outputWidth = ValidDimension(request.OutputWidth, sourceWidth);
        var outputHeight = ValidDimension(request.OutputHeight, sourceHeight);
        var outputBitDepth = ValidBitDepth(
            request.OutputBitDepth,
            inferredOutputBitDepth ?? sourceBitDepth);
        var outputRefFrames = request.OutputRefFrames
            ?? InferIntegerVideoOption(request.CommandLineArguments, "-refs");
        var outputFramerate = Math.Max(
            request.OutputFramerate ?? 0,
            InferFrameRate(request.CommandLineArguments) ?? 0);

        var largestFramePixels = Math.Max(
            sourceWidth * (long)sourceHeight,
            outputWidth * (long)outputHeight);
        var largestBitDepth = Math.Max(sourceBitDepth, outputBitDepth);

        var budgetMiB = BaseBudgetMiB(largestFramePixels, largestBitDepth);
        long pipelinePressureMiB = 0;

        // Tone mapping is the observed distinction between a roughly 1 GiB 4K HDR job and a
        // roughly 1.5 GiB one. Merely being HDR does not incur this band: Jellyfin must actually
        // have placed tonemap_cuda in the command line.
        if (features.UsesTonemap)
        {
            pipelinePressureMiB = largestFramePixels > UltraHdPixels
                ? ScaleAndRoundUp(UltraHdTonemapSurchargeMiB, largestFramePixels, UltraHdPixels)
                : largestFramePixels > QuadHdPixels ? UltraHdTonemapSurchargeMiB : 256;
        }

        if (features.UsesOtherFilters)
        {
            pipelinePressureMiB = Math.Max(
                pipelinePressureMiB,
                ScaleSurcharge(256, largestFramePixels));
        }

        if (string.Equals(request.OutputVideoCodec, "av1", StringComparison.OrdinalIgnoreCase))
        {
            pipelinePressureMiB = Math.Max(
                pipelinePressureMiB,
                ScaleSurcharge(256, largestFramePixels));
        }

        if (request.SourceRefFrames > 8
            || outputRefFrames > 8
            || request.SourceFramerate > 60
            || outputFramerate > 60)
        {
            pipelinePressureMiB = Math.Max(
                pipelinePressureMiB,
                ScaleSurcharge(256, largestFramePixels));
        }

        // These signals all describe pressure on the same decoder/filter/encoder surface pool.
        // Adding them as if they were independent allocations double-counts the pipeline and made
        // the measured 1339-1496 MiB 4K workload require 2048 MiB. Use the largest applicable
        // envelope, then apply the unknown-source floor to the completed budget.
        budgetMiB += pipelinePressureMiB;
        if (usedSourceFallback)
        {
            budgetMiB = Math.Max(UnknownSourceFloorMiB, budgetMiB);
        }

        budgetMiB = Math.Clamp(budgetMiB, 512, int.MaxValue);

        return new GpuVramEstimate(
            (int)budgetMiB,
            sourceWidth,
            sourceHeight,
            sourceBitDepth,
            outputWidth,
            outputHeight,
            outputBitDepth,
            features.UsesTonemap,
            usedFallback);
    }

    private static long BaseBudgetMiB(long largestFramePixels, int largestBitDepth)
    {
        long budgetMiB;
        if (largestFramePixels <= FullHdPixels)
        {
            budgetMiB = 512;
        }
        else if (largestFramePixels <= QuadHdPixels)
        {
            budgetMiB = 768;
        }
        else if (largestFramePixels <= UltraHdPixels)
        {
            budgetMiB = largestBitDepth > 8 ? UltraHd10BitBudgetMiB : UltraHd8BitBudgetMiB;
        }
        else
        {
            var ultraHdBudgetMiB = largestBitDepth > 8 ? UltraHd10BitBudgetMiB : UltraHd8BitBudgetMiB;
            budgetMiB = ScaleAndRoundUp(ultraHdBudgetMiB, largestFramePixels, UltraHdPixels);
        }

        return budgetMiB;
    }

    private static long ScaleAndRoundUp(int referenceMiB, long pixels, long referencePixels)
    {
        var scaledMiB = (decimal)referenceMiB * pixels / referencePixels;
        if (scaledMiB >= int.MaxValue)
        {
            return int.MaxValue;
        }

        var wholeMiB = (long)Math.Ceiling(scaledMiB);
        return Math.Min(int.MaxValue, ((wholeMiB + 255) / 256) * 256);
    }

    private static long ScaleSurcharge(int ultraHdMiB, long pixels)
        => pixels > UltraHdPixels
            ? ScaleAndRoundUp(ultraHdMiB, pixels, UltraHdPixels)
            : ultraHdMiB;

    private static int ValidDimension(int? value, int fallback)
        => IsDimension(value) ? value!.Value : fallback;

    private static bool IsDimension(int? value)
        => value is > 0;

    private static int ValidBitDepth(int? value, int fallback)
        => IsBitDepth(value) ? value!.Value : fallback;

    private static bool IsBitDepth(int? value)
        => value is >= 8 and <= 16;

    private static int? InferOutputBitDepth(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        var tokens = NvidiaTranscodeDetector.Tokenize(arguments);
        int? inferredBitDepth = null;
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var flag = tokens[i];
            var value = tokens[i + 1];
            if (flag.StartsWith("-pix_fmt", StringComparison.OrdinalIgnoreCase)
                && (flag.Length == 8 || flag[8] == ':'))
            {
                var pixelFormatBitDepth = InferPixelFormatBitDepth(value);
                if (pixelFormatBitDepth.HasValue)
                {
                    inferredBitDepth = Math.Max(inferredBitDepth ?? 0, pixelFormatBitDepth.Value);
                }
            }

            if (flag.StartsWith("-profile:v", StringComparison.OrdinalIgnoreCase)
                && (flag.Length == 10 || flag[10] == ':')
                && (value.Contains("main10", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("high10", StringComparison.OrdinalIgnoreCase)))
            {
                inferredBitDepth = Math.Max(inferredBitDepth ?? 0, 10);
            }
        }

        return inferredBitDepth;
    }

    private static int? InferPixelFormatBitDepth(string? pixelFormat)
    {
        if (string.IsNullOrWhiteSpace(pixelFormat))
        {
            return null;
        }

        var normalized = pixelFormat.Trim();
        foreach (var bitDepth in new[] { 16, 14, 12, 10, 9 }.Where(bitDepth =>
            normalized.Contains(
                bitDepth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)))
        {
            return bitDepth;
        }

        if (normalized.Equals("nv12", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("yuv", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("bgr", StringComparison.OrdinalIgnoreCase))
        {
            return 8;
        }

        return null;
    }

    private static int? InferIntegerVideoOption(string? arguments, string option)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        int? largestValue = null;
        var tokens = NvidiaTranscodeDetector.Tokenize(arguments);
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var flag = tokens[i];
            if (!flag.StartsWith(option, StringComparison.OrdinalIgnoreCase)
                || (flag.Length != option.Length && flag[option.Length] != ':')
                || !int.TryParse(
                    tokens[i + 1],
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed)
                || parsed < 0)
            {
                continue;
            }

            largestValue = Math.Max(largestValue ?? 0, parsed);
        }

        return largestValue;
    }

    private static float? InferFrameRate(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        float? largestRate = null;
        var tokens = NvidiaTranscodeDetector.Tokenize(arguments);
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var flag = tokens[i];
            if (!flag.StartsWith("-r", StringComparison.OrdinalIgnoreCase)
                || (flag.Length != 2 && flag[2] != ':')
                || !TryParseFrameRate(tokens[i + 1], out var rate))
            {
                continue;
            }

            largestRate = Math.Max(largestRate ?? 0, rate);
        }

        return largestRate;
    }

    private static bool TryParseFrameRate(string value, out float rate)
    {
        var separator = value.IndexOf('/');
        if (separator > 0
            && float.TryParse(
                value.AsSpan(0, separator),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var numerator)
            && float.TryParse(
                value.AsSpan(separator + 1),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var denominator)
            && denominator > 0)
        {
            rate = numerator / denominator;
            return rate >= 0;
        }

        return float.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out rate)
            && rate >= 0;
    }
}
