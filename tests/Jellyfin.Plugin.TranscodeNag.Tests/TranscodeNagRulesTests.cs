using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TranscodeNag.Configuration;
using Jellyfin.Plugin.TranscodeNag.Models;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.TranscodeNag.Tests;

public class TranscodeNagRulesTests
{
    [Fact]
    public void BuildConfiguredNagReasonMask_UsesDefaultsWhenConfigurationIsNull()
    {
        var mask = TranscodeNagRules.BuildConfiguredNagReasonMask(null);

        Assert.NotEqual((TranscodeReason)0, mask);
        Assert.True(mask.HasFlag(TranscodeReason.ContainerNotSupported));
        Assert.True(mask.HasFlag(TranscodeReason.DirectPlayError));
        Assert.False(mask.HasFlag(TranscodeReason.AudioIsExternal));
    }

    [Fact]
    public void BuildConfiguredNagReasonMask_IgnoresEmptyAndUnknownReasons()
    {
        var mask = TranscodeNagRules.BuildConfiguredNagReasonMask(
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

        Assert.True(TranscodeNagRules.ShouldNagTranscode(matching, config));
        Assert.False(TranscodeNagRules.ShouldNagTranscode(nonMatching, config));
        Assert.False(TranscodeNagRules.ShouldNagTranscode(noReasons, config));
    }

    [Fact]
    public void IsClientAllowed_AllowsAllClientsWhenIncludeListIsEmpty()
    {
        var config = new PluginConfiguration
        {
            ExcludedClientPatterns = new[] { "android tv" }
        };

        Assert.True(TranscodeNagRules.IsClientAllowed("Jellyfin Web", config));
        Assert.False(TranscodeNagRules.IsClientAllowed("Jellyfin Android TV", config));
    }

    [Fact]
    public void IsClientAllowed_RequiresIncludeMatchAndTreatsExcludeAsStronger()
    {
        var config = new PluginConfiguration
        {
            IncludedClientPatterns = new[] { " web ", "browser" },
            ExcludedClientPatterns = new[] { "chrome" }
        };

        Assert.True(TranscodeNagRules.IsClientAllowed("Jellyfin Web", config));
        Assert.True(TranscodeNagRules.IsClientAllowed("Firefox Browser", config));
        Assert.False(TranscodeNagRules.IsClientAllowed("Jellyfin Android TV", config));
        Assert.False(TranscodeNagRules.IsClientAllowed("Chrome Web", config));
        Assert.False(TranscodeNagRules.IsClientAllowed(null, config));
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

        Assert.True(TranscodeNagRules.IsMotdClientAllowed("Jellyfin Android TV", config));
        Assert.False(TranscodeNagRules.IsMotdClientAllowed("Jellyfin Web", config));
        Assert.False(TranscodeNagRules.IsMotdClientAllowed("Jellyfin Roku", config));
    }

    [Fact]
    public void IsMotdClientAllowed_AllowsEveryClientWhenNoMotdPatternsAreConfigured()
    {
        var config = new PluginConfiguration
        {
            ExcludedClientPatterns = new[] { "android tv" }
        };

        Assert.True(TranscodeNagRules.IsMotdClientAllowed("Jellyfin Android TV", config));
        Assert.True(TranscodeNagRules.IsMotdClientAllowed("Jellyfin Web", config));
        Assert.True(TranscodeNagRules.IsMotdClientAllowed(null, config));
    }

    [Fact]
    public void IsUserExcluded_MatchesDashedAndDashlessGuids()
    {
        var userId = Guid.NewGuid();

        Assert.True(TranscodeNagRules.IsUserExcluded(userId, new[] { userId.ToString("N") }));
        Assert.True(TranscodeNagRules.IsUserExcluded(userId, new[] { userId.ToString("D") }));
        Assert.True(TranscodeNagRules.IsUserExcluded(userId, new[] { " ", userId.ToString("N").ToUpperInvariant() }));
        Assert.False(TranscodeNagRules.IsUserExcluded(userId, new[] { Guid.NewGuid().ToString("N") }));
        Assert.False(TranscodeNagRules.IsUserExcluded(userId, Array.Empty<string>()));
        Assert.False(TranscodeNagRules.IsUserExcluded(userId, null));
        Assert.False(TranscodeNagRules.IsUserExcluded(Guid.Empty, new[] { Guid.Empty.ToString("N") }));
    }

    [Fact]
    public void IsLiveTvItem_DetectsLiveAndLiveTvItemTypes()
    {
        Assert.True(TranscodeNagRules.IsLiveTvItem(new BaseItemDto { IsLive = true, Type = BaseItemKind.Movie }));
        Assert.True(TranscodeNagRules.IsLiveTvItem(new BaseItemDto { Type = BaseItemKind.TvChannel }));
        Assert.True(TranscodeNagRules.IsLiveTvItem(new BaseItemDto { Type = BaseItemKind.LiveTvProgram }));
        Assert.True(TranscodeNagRules.IsLiveTvItem(new BaseItemDto { Type = BaseItemKind.TvProgram }));
        Assert.False(TranscodeNagRules.IsLiveTvItem(new BaseItemDto { Type = BaseItemKind.Movie }));
        Assert.False(TranscodeNagRules.IsLiveTvItem(null));
    }

    [Fact]
    public void IsItemAllowed_UsesLiveTvExclusionSetting()
    {
        var liveTvItem = new BaseItemDto { Type = BaseItemKind.TvChannel };

        Assert.True(TranscodeNagRules.IsItemAllowed(liveTvItem, new PluginConfiguration()));
        Assert.False(TranscodeNagRules.IsItemAllowed(
            liveTvItem,
            new PluginConfiguration { ExcludeLiveTv = true }));
        Assert.True(TranscodeNagRules.IsItemAllowed(
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

        Assert.True(TranscodeNagRules.IsStoredEventAllowed(
            new TranscodeEvent { Client = "Jellyfin Web" },
            config));
        Assert.False(TranscodeNagRules.IsStoredEventAllowed(
            new TranscodeEvent { Client = "Jellyfin Web", IsLiveTv = true },
            config));
        Assert.False(TranscodeNagRules.IsStoredEventAllowed(
            new TranscodeEvent { Client = "Jellyfin Android TV" },
            config));
    }

    [Fact]
    public void ResolveLoginNagWindow_MapsMonthAndFallsBackToWeek()
    {
        Assert.Equal((30, "month"), TranscodeNagRules.ResolveLoginNagWindow("Month"));
        Assert.Equal((7, "week"), TranscodeNagRules.ResolveLoginNagWindow("Week"));
        Assert.Equal((7, "week"), TranscodeNagRules.ResolveLoginNagWindow("anything-else"));
    }

    [Fact]
    public void FormatLoginNagMessage_ReplacesBothPlaceholders()
    {
        var message = TranscodeNagRules.FormatLoginNagMessage(
            "Bad transcodes: {{transcodes}} this {{timewindow}}.",
            4,
            "month");

        Assert.Equal("Bad transcodes: 4 this month.", message);
    }
}
