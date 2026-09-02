using System;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using Jellyfin.Plugin.TranscodeGuard.Models;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.TranscodeGuard;

internal static class TranscodeGuardRules
{
    internal static TranscodeReason BuildConfiguredNagReasonMask(string[]? configuredReasonNames)
    {
        var selectedReasonNames = configuredReasonNames ?? PluginConfiguration.GetDefaultAlertTranscodeReasons();
        var reasonMask = (TranscodeReason)0;

        foreach (var reasonName in selectedReasonNames)
        {
            if (string.IsNullOrWhiteSpace(reasonName))
            {
                continue;
            }

            if (Enum.TryParse(reasonName, true, out TranscodeReason parsedReason))
            {
                reasonMask |= parsedReason;
            }
        }

        return reasonMask;
    }

    internal static bool HasConfiguredClientPatterns(string[]? configuredPatterns)
    {
        return configuredPatterns?.Any(pattern => !string.IsNullOrWhiteSpace(pattern)) == true;
    }

    internal static bool MatchesConfiguredClientPatterns(string? clientName, string[]? configuredPatterns)
    {
        if (string.IsNullOrWhiteSpace(clientName) || configuredPatterns == null)
        {
            return false;
        }

        foreach (var pattern in configuredPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            if (clientName.IndexOf(pattern.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsClientAllowed(string? clientName, string[]? includedPatterns, string[]? excludedPatterns)
    {
        if (MatchesConfiguredClientPatterns(clientName, excludedPatterns))
        {
            return false;
        }

        if (!HasConfiguredClientPatterns(includedPatterns))
        {
            return true;
        }

        return MatchesConfiguredClientPatterns(clientName, includedPatterns);
    }

    internal static bool IsClientAllowed(string? clientName, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return IsClientAllowed(clientName, config.IncludedClientPatterns, config.ExcludedClientPatterns);
    }

    internal static bool IsMotdClientAllowed(string? clientName, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return IsClientAllowed(clientName, config.MotdIncludedClientPatterns, config.MotdExcludedClientPatterns);
    }

    internal static bool IsUserExcluded(Guid userId, string[]? excludedUserIds)
    {
        if (userId == Guid.Empty || excludedUserIds == null || excludedUserIds.Length == 0)
        {
            return false;
        }

        foreach (var excludedUserId in excludedUserIds)
        {
            if (string.IsNullOrWhiteSpace(excludedUserId))
            {
                continue;
            }

            // Accept both "N" (Jellyfin's wire format) and dashed GUIDs so hand-edited configs still work.
            if (Guid.TryParse(excludedUserId.Trim(), out var parsedUserId) && parsedUserId == userId)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsLiveTvItem(BaseItemDto? item)
    {
        if (item == null)
        {
            return false;
        }

        if (item.IsLive == true)
        {
            return true;
        }

        return item.Type == BaseItemKind.TvChannel
            || item.Type == BaseItemKind.TvProgram
            || item.Type == BaseItemKind.LiveTvChannel
            || item.Type == BaseItemKind.LiveTvProgram;
    }

    internal static bool IsItemAllowed(BaseItemDto? item, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return !config.ExcludeLiveTv || !IsLiveTvItem(item);
    }

    internal static bool IsStoredEventAllowed(TranscodeEvent transcodeEvent, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(transcodeEvent);
        ArgumentNullException.ThrowIfNull(config);

        return IsClientAllowed(transcodeEvent.Client, config)
            && (!config.ExcludeLiveTv || !transcodeEvent.IsLiveTv);
    }

    internal static bool ShouldNagTranscode(TranscodingInfo transcodeInfo, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(transcodeInfo);
        ArgumentNullException.ThrowIfNull(config);

        return MatchesConfiguredNagReasons(transcodeInfo.TranscodeReasons, config);
    }

    /// <summary>
    /// Decides whether a transcode counts as one of the failures this plugin cares about. The one
    /// place that answer is defined, so what the login nag counts and what the transcode limit
    /// refuses can never drift apart.
    /// </summary>
    /// <param name="transcodeReasons">Jellyfin's reasons for the transcode.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>True when at least one configured reason is active.</returns>
    internal static bool MatchesConfiguredNagReasons(TranscodeReason transcodeReasons, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // If no transcode reasons specified, it's likely bitrate limiting - don't nag.
        if (transcodeReasons == (TranscodeReason)0)
        {
            return false;
        }

        var enabledNagReasons = BuildConfiguredNagReasonMask(config.AlertTranscodeReasons);
        if (enabledNagReasons == (TranscodeReason)0)
        {
            return false;
        }

        return (transcodeReasons & enabledNagReasons) != 0;
    }

    internal static (int Days, string Label) ResolveLoginNagWindow(string? configuredTimeWindow)
    {
        return configuredTimeWindow == "Month" ? (30, "month") : (7, "week");
    }

    internal static string FormatLoginNagMessage(string template, int badTranscodeCount, string timeWindowLabel)
    {
        return template
            .Replace("{{transcodes}}", badTranscodeCount.ToString(), StringComparison.Ordinal)
            .Replace("{{timewindow}}", timeWindowLabel, StringComparison.Ordinal);
    }

    internal static string FormatTranscodeLimitMessage(string template, int badTranscodeCount, string timeWindowLabel, int limit)
    {
        return FormatLoginNagMessage(template, badTranscodeCount, timeWindowLabel)
            .Replace("{{limit}}", limit.ToString(), StringComparison.Ordinal);
    }
}
