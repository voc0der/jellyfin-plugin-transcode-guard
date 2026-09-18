using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.TranscodeGuard.Browser;

/// <summary>
/// Decides which sessions get the browser install warning and builds what the injected script
/// renders. The warning rides on the playback nag's own DisplayMessage as extra arguments, so
/// Jellyfin Web still shows its normal popup wherever the script is not running.
/// </summary>
internal static class BrowserNagRules
{
    /// <summary>
    /// The client name Jellyfin Web reports for itself.
    /// </summary>
    internal const string JellyfinWebClient = "Jellyfin Web";

    internal const int MinAutoCloseSeconds = 5;
    internal const int MaxAutoCloseSeconds = 180;

    // DisplayMessage argument names read by Browser/TranscodeGuardBrowser.js. Prefixed so they
    // cannot collide with Header, Text, TimeoutMs, or anything Jellyfin adds later.
    internal const string MarkerArgument = "TranscodeGuardNag";
    internal const string MarkerValue = "1";
    internal const string NagIdArgument = "TranscodeGuardNagId";
    internal const string TitleArgument = "TranscodeGuardTitle";
    internal const string MessageArgument = "TranscodeGuardMessage";
    internal const string ReasonArgument = "TranscodeGuardReason";
    internal const string InstallUrlArgument = "TranscodeGuardInstallUrl";
    internal const string AutoCloseSecondsArgument = "TranscodeGuardAutoCloseSeconds";

    private const string DefaultTitle = "Transcoding detected";
    private const string DefaultMessage = "Your browser is causing this stream to be transcoded. For improved playback, install the recommended client.";

    private static readonly Regex WordBoundary = new("(?<=[a-z])(?=[A-Z])", RegexOptions.Compiled);

    /// <summary>
    /// The one test for "this session is a browser". An allowlist on the client name Jellyfin
    /// recorded for the session: Jellyfin Media Player, the Android app, and other wrappers run the
    /// same web UI but report their own client name, and DeviceName ("Firefox", "Chrome") says
    /// nothing reliable about which client is in use, so it is never consulted.
    /// </summary>
    /// <param name="session">The session to classify.</param>
    /// <returns>True only for Jellyfin Web.</returns>
    internal static bool IsBrowserSession(SessionInfo? session)
        => string.Equals(session?.Client, JellyfinWebClient, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decides whether a playback nag should carry the browser warning. Whether the nag is sent at
    /// all is still decided by the existing playback nag rules.
    /// </summary>
    /// <param name="session">The session being nagged.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>True when the injected script should upgrade the nag to the install warning.</returns>
    internal static bool ShouldUseBrowserNag(SessionInfo? session, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.EnableBrowserInstallNag && IsBrowserSession(session);
    }

    /// <summary>
    /// Returns the configured install link if it is an absolute http or https URL, otherwise null.
    /// Anything else - javascript:, data:, relative paths - would be a broken or unsafe link.
    /// </summary>
    /// <param name="configuredUrl">The URL from the configuration.</param>
    /// <returns>The normalized URL, or null when the button should be left out.</returns>
    internal static string? NormalizeInstallUrl(string? configuredUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(configuredUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    internal static int ClampAutoCloseSeconds(int seconds)
        => Math.Clamp(seconds, MinAutoCloseSeconds, MaxAutoCloseSeconds);

    /// <summary>
    /// Describes the configured reasons behind a transcode in plain words, for example
    /// "Video codec not supported, audio codec not supported".
    /// </summary>
    /// <param name="transcodeReasons">Jellyfin's reasons for the transcode.</param>
    /// <param name="config">Plugin configuration; only the reasons that trigger nags are described.</param>
    /// <returns>The description, or an empty string when no configured reason is active.</returns>
    internal static string DescribeReasons(TranscodeReason transcodeReasons, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var nagReasons = transcodeReasons & TranscodeGuardRules.BuildConfiguredNagReasonMask(config.AlertTranscodeReasons);

        var descriptions = Enum.GetValues<TranscodeReason>()
            .Where(reason => reason != 0 && (nagReasons & reason) == reason)
            .Select(reason => WordBoundary.Split(reason.ToString()))
            .Select(words => string.Join(
                ' ',
                words.Select((word, index) => index == 0 ? word : word.ToLowerInvariant())))
            .ToList();

        if (descriptions.Count == 0)
        {
            return string.Empty;
        }

        // Only the first description starts the sentence.
        return string.Join(
            ", ",
            descriptions.Select((description, index) => index == 0
                ? description
                : char.ToLowerInvariant(description[0]) + description[1..]));
    }

    /// <summary>
    /// Builds the extra DisplayMessage arguments the injected script turns into the install warning.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="transcodeReasons">Jellyfin's reasons for the transcode.</param>
    /// <param name="nagId">Identifies this nag, so sticky resends of it are shown once.</param>
    /// <returns>The arguments to add to the playback nag's DisplayMessage.</returns>
    internal static Dictionary<string, string> BuildArguments(
        PluginConfiguration config,
        TranscodeReason transcodeReasons,
        Guid nagId)
    {
        ArgumentNullException.ThrowIfNull(config);

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MarkerArgument] = MarkerValue,
            [NagIdArgument] = nagId.ToString("N", CultureInfo.InvariantCulture),

            // An admin who blanks these fields should still get a usable warning.
            [TitleArgument] = Fallback(config.BrowserNagTitle, DefaultTitle),
            [MessageArgument] = Fallback(config.BrowserNagMessage, DefaultMessage),
            [AutoCloseSecondsArgument] = ClampAutoCloseSeconds(config.BrowserNagAutoCloseSeconds).ToString(CultureInfo.InvariantCulture)
        };

        var reason = DescribeReasons(transcodeReasons, config);
        if (reason.Length > 0)
        {
            arguments[ReasonArgument] = reason;
        }

        var installUrl = NormalizeInstallUrl(config.BrowserInstallUrl);
        if (installUrl != null)
        {
            arguments[InstallUrlArgument] = installUrl;
        }

        return arguments;
    }

    private static string Fallback(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
