using System;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.TranscodeGuard.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    private static readonly string[] DefaultAlertReasons =
    {
        nameof(TranscodeReason.ContainerNotSupported),
        nameof(TranscodeReason.VideoCodecNotSupported),
        nameof(TranscodeReason.AudioCodecNotSupported),
        nameof(TranscodeReason.SubtitleCodecNotSupported),
        nameof(TranscodeReason.VideoProfileNotSupported),
        nameof(TranscodeReason.VideoLevelNotSupported),
        nameof(TranscodeReason.VideoResolutionNotSupported),
        nameof(TranscodeReason.VideoBitDepthNotSupported),
        nameof(TranscodeReason.VideoFramerateNotSupported),
        nameof(TranscodeReason.RefFramesNotSupported),
        nameof(TranscodeReason.AnamorphicVideoNotSupported),
        nameof(TranscodeReason.InterlacedVideoNotSupported),
        nameof(TranscodeReason.AudioChannelsNotSupported),
        nameof(TranscodeReason.AudioProfileNotSupported),
        nameof(TranscodeReason.AudioSampleRateNotSupported),
        nameof(TranscodeReason.SecondaryAudioNotSupported),
        nameof(TranscodeReason.VideoRangeTypeNotSupported),
        nameof(TranscodeReason.DirectPlayError)
    };

    public static string[] GetDefaultAlertTranscodeReasons()
    {
        return (string[])DefaultAlertReasons.Clone();
    }

    public string NagMessage { get; set; } = "Your client is transcoding because it doesn't support the video format. Consider using a client that supports direct play (like mpv, VLC, or Jellyfin Media Player) to reduce server load and improve quality!";

    public bool UseStickyPlaybackMessages { get; set; } = false;

    public int MessageTimeoutMs { get; set; } = 10000;

    public bool EnableLogging { get; set; } = true;

    public int DelaySeconds { get; set; } = 5;

    public bool EnableLoginNag { get; set; } = true;

    public int LoginNagThreshold { get; set; } = 5;

    public bool ExcludeLiveTv { get; set; } = false;

    public string LoginNagTimeWindow { get; set; } = "Week";

    public string LoginNagMessage { get; set; } = "You've transcoded {{transcodes}} videos in the last {{timewindow}} due to unsupported formats. Consider switching to mpv, VLC, or Jellyfin Media Player to improve quality and reduce server load!";

    public bool UseStickyLoginNagMessages { get; set; } = false;

    public string[] AlertTranscodeReasons { get; set; } = GetDefaultAlertTranscodeReasons();

    public ReasonMessageOverride[] ReasonMessageOverrides { get; set; } = Array.Empty<ReasonMessageOverride>();

    public string[] IncludedClientPatterns { get; set; } = Array.Empty<string>();

    public string[] ExcludedClientPatterns { get; set; } = Array.Empty<string>();

    public string[] ExcludedUserIds { get; set; } = Array.Empty<string>();

    public bool EnableMotd { get; set; } = false;

    public string MotdMessage { get; set; } = "Welcome back! Check the announcements channel for server news and planned maintenance.";

    public bool UseStickyMotdMessages { get; set; } = false;

    public string[] MotdExcludedUserIds { get; set; } = Array.Empty<string>();

    public string[] MotdIncludedClientPatterns { get; set; } = Array.Empty<string>();

    public string[] MotdExcludedClientPatterns { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets a value indicating whether GPU-backed video transcodes are refused when free VRAM is low.
    /// Defaults to off so upgrading the plugin cannot change playback behaviour on its own.
    /// </summary>
    public bool EnableGpuResourceGuard { get; set; } = false;

    /// <summary>
    /// Gets or sets the fallback zero-based NVIDIA GPU index. An explicit FFmpeg selection wins.
    /// </summary>
    public int GpuIndex { get; set; } = 0;

    /// <summary>
    /// Gets or sets how long the nvidia-smi query may run before it is abandoned.
    /// </summary>
    public int GpuCheckTimeoutMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Gets or sets an explicit nvidia-smi path. Empty means resolve "nvidia-smi" from PATH.
    /// </summary>
    public string NvidiaSmiPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the popup title shown to a client whose transcode was refused.
    /// </summary>
    public string GpuGuardDeniedHeader { get; set; } = "Transcoding unavailable";

    /// <summary>
    /// Gets or sets the popup body shown to a client whose transcode was refused.
    /// Deliberately free of VRAM figures, encoder names, and other server-side detail.
    /// </summary>
    public string GpuGuardDeniedMessage { get; set; } = "GPU resources are currently busy. Please try again later or use Direct Play.";

    public bool UseStickyGpuGuardMessages { get; set; } = false;
}

public class ReasonMessageOverride
{
    public string ReasonName { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}
