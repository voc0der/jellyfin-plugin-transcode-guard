using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using Jellyfin.Plugin.TranscodeGuard.Models;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

public class TranscodeGuardRulesTests
{
    [Fact]
    public void BuildConfiguredNagReasonMask_UsesDefaultsWhenConfigurationIsNull()
    {
        var mask = TranscodeGuardRules.BuildConfiguredNagReasonMask(null);

        Assert.NotEqual((TranscodeReason)0, mask);
        Assert.True(mask.HasFlag(TranscodeReason.ContainerNotSupported));
        Assert.True(mask.HasFlag(TranscodeReason.DirectPlayError));
        Assert.False(mask.HasFlag(TranscodeReason.AudioIsExternal));
    }

    [Fact]
    public void BuildConfiguredNagReasonMask_IgnoresEmptyAndUnknownReasons()
    {
        var mask = TranscodeGuardRules.BuildConfiguredNagReasonMask(
            new[]
            {
                "VideoCodecNotSupported",
                "  ",
                "not-a-real-reason",
                "AudioCodecNotSupported"
            });

        Assert.Equal(
            TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported,
            mask);
    }

    [Fact]
    public void ShouldNagTranscode_ReturnsTrueOnlyWhenReasonsOverlapConfiguredMask()
    {
        var config = new PluginConfiguration
        {
            AlertTranscodeReasons = new[]
            {
                nameof(TranscodeReason.VideoCodecNotSupported),
                nameof(TranscodeReason.AudioCodecNotSupported)
            }
        };

        var matching = new TranscodingInfo
        {
            TranscodeReasons = TranscodeReason.VideoCodecNotSupported | TranscodeReason.ContainerNotSupported
        };
        var nonMatching = new TranscodingInfo
        {
            TranscodeReasons = TranscodeReason.ContainerNotSupported
        };
        var noReasons = new TranscodingInfo
        {
            TranscodeReasons = (TranscodeReason)0
        };

        Assert.True(TranscodeGuardRules.ShouldNagTranscode(matching, config));
        Assert.False(TranscodeGuardRules.ShouldNagTranscode(nonMatching, config));
        Assert.False(TranscodeGuardRules.ShouldNagTranscode(noReasons, config));
    }

    [Fact]
    public void IsClientAllowed_AllowsAllClientsWhenIncludeListIsEmpty()
    {
        var config = new PluginConfiguration
        {
            ExcludedClientPatterns = new[] { "android tv" }
        };

        Assert.True(TranscodeGuardRules.IsClientAllowed("Jellyfin Web", config));
        Assert.False(TranscodeGuardRules.IsClientAllowed("Jellyfin Android TV", config));
    }

    [Fact]
    public void IsClientAllowed_RequiresIncludeMatchAndTreatsExcludeAsStronger()
    {
        var config = new PluginConfiguration
        {
            IncludedClientPatterns = new[] { " web ", "browser" },
            ExcludedClientPatterns = new[] { "chrome" }
        };

        Assert.True(TranscodeGuardRules.IsClientAllowed("Jellyfin Web", config));
        Assert.True(TranscodeGuardRules.IsClientAllowed("Firefox Browser", config));
        Assert.False(TranscodeGuardRules.IsClientAllowed("Jellyfin Android TV", config));
        Assert.False(TranscodeGuardRules.IsClientAllowed("Chrome Web", config));
        Assert.False(TranscodeGuardRules.IsClientAllowed(null, config));
    }

    [Fact]
    public void IsMotdClientAllowed_UsesMotdPatternsAndIgnoresNagPatterns()
    {
        var config = new PluginConfiguration
        {
            IncludedClientPatterns = new[] { "web" },
            ExcludedClientPatterns = new[] { "android tv" },
            MotdIncludedClientPatterns = new[] { "android tv" },
            MotdExcludedClientPatterns = new[] { "roku" }
        };

        Assert.True(TranscodeGuardRules.IsMotdClientAllowed("Jellyfin Android TV", config));
        Assert.False(TranscodeGuardRules.IsMotdClientAllowed("Jellyfin Web", config));
        Assert.False(TranscodeGuardRules.IsMotdClientAllowed("Jellyfin Roku", config));
    }

    [Fact]
    public void IsMotdClientAllowed_AllowsEveryClientWhenNoMotdPatternsAreConfigured()
    {
        var config = new PluginConfiguration
        {
            ExcludedClientPatterns = new[] { "android tv" }
        };

        Assert.True(TranscodeGuardRules.IsMotdClientAllowed("Jellyfin Android TV", config));
        Assert.True(TranscodeGuardRules.IsMotdClientAllowed("Jellyfin Web", config));
        Assert.True(TranscodeGuardRules.IsMotdClientAllowed(null, config));
    }

    [Fact]
    public void IsUserExcluded_MatchesDashedAndDashlessGuids()
    {
        var userId = Guid.NewGuid();

        Assert.True(TranscodeGuardRules.IsUserExcluded(userId, new[] { userId.ToString("N") }));
        Assert.True(TranscodeGuardRules.IsUserExcluded(userId, new[] { userId.ToString("D") }));
        Assert.True(TranscodeGuardRules.IsUserExcluded(userId, new[] { " ", userId.ToString("N").ToUpperInvariant() }));
        Assert.False(TranscodeGuardRules.IsUserExcluded(userId, new[] { Guid.NewGuid().ToString("N") }));
        Assert.False(TranscodeGuardRules.IsUserExcluded(userId, Array.Empty<string>()));
        Assert.False(TranscodeGuardRules.IsUserExcluded(userId, null));
        Assert.False(TranscodeGuardRules.IsUserExcluded(Guid.Empty, new[] { Guid.Empty.ToString("N") }));
    }

    [Fact]
    public void IsLiveTvItem_DetectsLiveAndLiveTvItemTypes()
    {
        Assert.True(TranscodeGuardRules.IsLiveTvItem(new BaseItemDto { IsLive = true, Type = BaseItemKind.Movie }));
        Assert.True(TranscodeGuardRules.IsLiveTvItem(new BaseItemDto { Type = BaseItemKind.TvChannel }));
        Assert.True(TranscodeGuardRules.IsLiveTvItem(new BaseItemDto { Type = BaseItemKind.LiveTvProgram }));
        Assert.True(TranscodeGuardRules.IsLiveTvItem(new BaseItemDto { Type = BaseItemKind.TvProgram }));
        Assert.False(TranscodeGuardRules.IsLiveTvItem(new BaseItemDto { Type = BaseItemKind.Movie }));
        Assert.False(TranscodeGuardRules.IsLiveTvItem(null));
    }

    [Fact]
    public void IsItemAllowed_UsesLiveTvExclusionSetting()
    {
        var liveTvItem = new BaseItemDto { Type = BaseItemKind.TvChannel };

        Assert.True(TranscodeGuardRules.IsItemAllowed(liveTvItem, new PluginConfiguration()));
        Assert.False(TranscodeGuardRules.IsItemAllowed(
            liveTvItem,
            new PluginConfiguration { ExcludeLiveTv = true }));
        Assert.True(TranscodeGuardRules.IsItemAllowed(
            new BaseItemDto { Type = BaseItemKind.Movie },
            new PluginConfiguration { ExcludeLiveTv = true }));
    }

    [Fact]
    public void IsStoredEventAllowed_AppliesClientAndLiveTvFilters()
    {
        var config = new PluginConfiguration
        {
            ExcludeLiveTv = true,
            ExcludedClientPatterns = new[] { "android tv" }
        };

        Assert.True(TranscodeGuardRules.IsStoredEventAllowed(
            new TranscodeEvent { Client = "Jellyfin Web" },
            config));
        Assert.False(TranscodeGuardRules.IsStoredEventAllowed(
            new TranscodeEvent { Client = "Jellyfin Web", IsLiveTv = true },
            config));
        Assert.False(TranscodeGuardRules.IsStoredEventAllowed(
            new TranscodeEvent { Client = "Jellyfin Android TV" },
            config));
    }

    [Fact]
    public void ResolveLoginNagWindow_MapsMonthAndFallsBackToWeek()
    {
        Assert.Equal((30, "month"), TranscodeGuardRules.ResolveLoginNagWindow("Month"));
        Assert.Equal((7, "week"), TranscodeGuardRules.ResolveLoginNagWindow("Week"));
        Assert.Equal((7, "week"), TranscodeGuardRules.ResolveLoginNagWindow("anything-else"));
    }

    [Fact]
    public void FormatLoginNagMessage_ReplacesBothPlaceholders()
    {
        var message = TranscodeGuardRules.FormatLoginNagMessage(
            "Bad transcodes: {{transcodes}} this {{timewindow}}.",
            4,
            "month");

        Assert.Equal("Bad transcodes: 4 this month.", message);
    }
}
