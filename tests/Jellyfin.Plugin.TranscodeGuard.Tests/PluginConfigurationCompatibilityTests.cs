using System.Xml.Serialization;
using Jellyfin.Plugin.TranscodeGuard.Configuration;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

public class PluginConfigurationCompatibilityTests
{
    [Fact]
    public void StickyMessagesAreOptInByDefault()
    {
        var config = new PluginConfiguration();

        Assert.False(config.UseStickyPlaybackMessages);
        Assert.False(config.UseStickyLoginNagMessages);
        Assert.False(config.UseStickyMotdMessages);
        Assert.False(config.UseStickyGpuGuardMessages);
    }

    [Fact]
    public void StickyMessageSettingsRoundTripThroughXml()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var config = new PluginConfiguration
        {
            UseStickyPlaybackMessages = true,
            UseStickyLoginNagMessages = true,
            UseStickyMotdMessages = true,
            UseStickyGpuGuardMessages = true
        };

        using var writer = new StringWriter();
        serializer.Serialize(writer, config);

        using var reader = new StringReader(writer.ToString());
        var roundTripped = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.True(roundTripped.UseStickyPlaybackMessages);
        Assert.True(roundTripped.UseStickyLoginNagMessages);
        Assert.True(roundTripped.UseStickyMotdMessages);
        Assert.True(roundTripped.UseStickyGpuGuardMessages);
    }

    [Fact]
    public void LegacyGpuThresholdElementDoesNotBreakConfigurationLoading()
    {
        const string LegacyXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration>
              <EnableGpuResourceGuard>true</EnableGpuResourceGuard>
              <GpuIndex>1</GpuIndex>
              <MinimumFreeGpuMemoryMiB>1500</MinimumFreeGpuMemoryMiB>
              <GpuCheckTimeoutMilliseconds>1750</GpuCheckTimeoutMilliseconds>
              <GpuGuardDeniedHeader>Busy</GpuGuardDeniedHeader>
            </PluginConfiguration>
            """;
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        using var reader = new StringReader(LegacyXml);
        var config = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.True(config.EnableGpuResourceGuard);
        Assert.Equal(1, config.GpuIndex);
        Assert.Equal(1750, config.GpuCheckTimeoutMilliseconds);
        Assert.Equal("Busy", config.GpuGuardDeniedHeader);
        Assert.False(config.UseStickyPlaybackMessages);
        Assert.False(config.UseStickyLoginNagMessages);
        Assert.False(config.UseStickyMotdMessages);
        Assert.False(config.UseStickyGpuGuardMessages);

        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        Assert.DoesNotContain("MinimumFreeGpuMemoryMiB", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TranscodeLimitIsOptInAndSitsAboveTheDefaultLoginNagThreshold()
    {
        var config = new PluginConfiguration();

        // Upgrading the plugin must not start failing anyone's playback, and out of the box the
        // limit must leave room for the nag to warn first.
        Assert.False(config.EnableTranscodeLimit);
        Assert.False(config.UseStickyTranscodeLimitMessages);
        Assert.True(config.TranscodeLimitThreshold > config.LoginNagThreshold);
    }

    [Fact]
    public void TranscodeLimitSettingsRoundTripThroughXml()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var config = new PluginConfiguration
        {
            EnableTranscodeLimit = true,
            TranscodeLimitThreshold = 12,
            TranscodeLimitHeader = "Blocked",
            TranscodeLimitMessage = "{{transcodes}} of {{limit}}",
            UseStickyTranscodeLimitMessages = true
        };

        using var writer = new StringWriter();
        serializer.Serialize(writer, config);

        using var reader = new StringReader(writer.ToString());
        var roundTripped = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.True(roundTripped.EnableTranscodeLimit);
        Assert.Equal(12, roundTripped.TranscodeLimitThreshold);
        Assert.Equal("Blocked", roundTripped.TranscodeLimitHeader);
        Assert.Equal("{{transcodes}} of {{limit}}", roundTripped.TranscodeLimitMessage);
        Assert.True(roundTripped.UseStickyTranscodeLimitMessages);
    }

    [Fact]
    public void ConfigurationSavedBeforeTheTranscodeLimitExistedLoadsWithItSwitchedOff()
    {
        const string LegacyXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration>
              <EnableLoginNag>true</EnableLoginNag>
              <LoginNagThreshold>5</LoginNagThreshold>
            </PluginConfiguration>
            """;
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        using var reader = new StringReader(LegacyXml);
        var config = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.False(config.EnableTranscodeLimit);
        Assert.Equal(10, config.TranscodeLimitThreshold);
    }

    [Fact]
    public void SimultaneousTranscodeLimitIsOptInWithNoMaximumSet()
    {
        var config = new PluginConfiguration();

        // Off, and even switched on it would limit nothing until a maximum is chosen.
        Assert.False(config.EnableConcurrentTranscodeLimit);
        Assert.Equal(0, config.MaxConcurrentTranscodes);
        Assert.Empty(config.UserConcurrentTranscodeLimits);
        Assert.False(config.UseStickyConcurrentTranscodeLimitMessages);
        Assert.Contains("{{active}}", config.ConcurrentTranscodeLimitMessage, StringComparison.Ordinal);
        Assert.Contains("{{limit}}", config.ConcurrentTranscodeLimitMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SimultaneousTranscodeLimitSettingsRoundTripThroughXml()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var config = new PluginConfiguration
        {
            EnableConcurrentTranscodeLimit = true,
            MaxConcurrentTranscodes = 2,
            UserConcurrentTranscodeLimits = new[]
            {
                new UserConcurrentTranscodeLimit { UserId = "4e6ea05698214e7c940d2af1806f1f8e", MaxTranscodes = 4 },
                new UserConcurrentTranscodeLimit { UserId = "9f1b3e6c8a0d47259c4e7b2f5a8d0c31", MaxTranscodes = 1 }
            },
            ConcurrentTranscodeLimitHeader = "Busy",
            ConcurrentTranscodeLimitMessage = "{{active}} of {{limit}}",
            UseStickyConcurrentTranscodeLimitMessages = true
        };

        using var writer = new StringWriter();
        serializer.Serialize(writer, config);

        using var reader = new StringReader(writer.ToString());
        var roundTripped = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.True(roundTripped.EnableConcurrentTranscodeLimit);
        Assert.Equal(2, roundTripped.MaxConcurrentTranscodes);
        Assert.Collection(
            roundTripped.UserConcurrentTranscodeLimits,
            entry =>
            {
                Assert.Equal("4e6ea05698214e7c940d2af1806f1f8e", entry.UserId);
                Assert.Equal(4, entry.MaxTranscodes);
            },
            entry =>
            {
                Assert.Equal("9f1b3e6c8a0d47259c4e7b2f5a8d0c31", entry.UserId);
                Assert.Equal(1, entry.MaxTranscodes);
            });
        Assert.Equal("Busy", roundTripped.ConcurrentTranscodeLimitHeader);
        Assert.Equal("{{active}} of {{limit}}", roundTripped.ConcurrentTranscodeLimitMessage);
        Assert.True(roundTripped.UseStickyConcurrentTranscodeLimitMessages);
    }

    [Fact]
    public void ConfigurationSavedBeforeTheSimultaneousLimitExistedLoadsWithItSwitchedOff()
    {
        const string OlderXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration>
              <EnableTranscodeLimit>true</EnableTranscodeLimit>
              <TranscodeLimitThreshold>12</TranscodeLimitThreshold>
            </PluginConfiguration>
            """;
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        using var reader = new StringReader(OlderXml);
        var config = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.False(config.EnableConcurrentTranscodeLimit);
        Assert.Equal(0, config.MaxConcurrentTranscodes);
        Assert.Empty(config.UserConcurrentTranscodeLimits);
        Assert.Equal(12, config.TranscodeLimitThreshold);
    }

    [Fact]
    public void PausedTranscodeReaperIsOptInAndDefaultsTo25Minutes()
    {
        var config = new PluginConfiguration();

        Assert.False(config.EnablePausedTranscodeReaper);
        Assert.Equal(25, config.PausedTranscodeTimeoutMinutes);
        Assert.Equal(2, config.PausedTranscodeWarningMinutes);
        Assert.False(config.UseStickyPausedTranscodeMessages);
        Assert.False(config.ReapPausedDirectPlay);
        Assert.Empty(config.PausedTranscodeExcludedUserIds);
        Assert.Contains("{{minutes}}", config.PausedTranscodeWarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void PausedTranscodeReaperSettingsRoundTripThroughXml()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var config = new PluginConfiguration
        {
            EnablePausedTranscodeReaper = true,
            PausedTranscodeTimeoutMinutes = 40,
            PausedTranscodeWarningMinutes = 5,
            PausedTranscodeWarningHeader = "Wake up",
            PausedTranscodeWarningMessage = "Stopping in {{minutes}}.",
            UseStickyPausedTranscodeMessages = true,
            ReapPausedDirectPlay = true,
            PausedTranscodeExcludedUserIds = new[] { "abc" }
        };

        using var writer = new StringWriter();
        serializer.Serialize(writer, config);

        using var reader = new StringReader(writer.ToString());
        var roundTripped = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.True(roundTripped.EnablePausedTranscodeReaper);
        Assert.Equal(40, roundTripped.PausedTranscodeTimeoutMinutes);
        Assert.Equal(5, roundTripped.PausedTranscodeWarningMinutes);
        Assert.Equal("Wake up", roundTripped.PausedTranscodeWarningHeader);
        Assert.Equal("Stopping in {{minutes}}.", roundTripped.PausedTranscodeWarningMessage);
        Assert.True(roundTripped.UseStickyPausedTranscodeMessages);
        Assert.True(roundTripped.ReapPausedDirectPlay);
        Assert.Equal(new[] { "abc" }, roundTripped.PausedTranscodeExcludedUserIds);
    }

    [Fact]
    public void BrowserInstallWarningIsOptInAndClosesItselfAfterAMinute()
    {
        var config = new PluginConfiguration();

        Assert.False(config.EnableBrowserInstallNag);
        Assert.Equal(string.Empty, config.BrowserInstallUrl);
        Assert.Equal("Transcoding detected", config.BrowserNagTitle);
        Assert.Equal(60, config.BrowserNagAutoCloseSeconds);
    }

    [Fact]
    public void BrowserInstallWarningSettingsRoundTripThroughXml()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var config = new PluginConfiguration
        {
            EnableBrowserInstallNag = true,
            BrowserInstallUrl = "https://example.com/client",
            BrowserNagTitle = "Use the app",
            BrowserNagMessage = "Please install it.",
            BrowserNagAutoCloseSeconds = 120
        };

        using var writer = new StringWriter();
        serializer.Serialize(writer, config);

        using var reader = new StringReader(writer.ToString());
        var roundTripped = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.True(roundTripped.EnableBrowserInstallNag);
        Assert.Equal("https://example.com/client", roundTripped.BrowserInstallUrl);
        Assert.Equal("Use the app", roundTripped.BrowserNagTitle);
        Assert.Equal("Please install it.", roundTripped.BrowserNagMessage);
        Assert.Equal(120, roundTripped.BrowserNagAutoCloseSeconds);
    }

    [Fact]
    public void ConfigurationSavedBeforeTheReaperExistedStaysOptedOut()
    {
        const string OlderXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration>
              <EnableLoginNag>true</EnableLoginNag>
              <LoginNagThreshold>7</LoginNagThreshold>
            </PluginConfiguration>
            """;
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        using var reader = new StringReader(OlderXml);
        var config = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        // An upgrade must never start ending anyone's playback on its own.
        Assert.False(config.EnablePausedTranscodeReaper);
        Assert.False(config.ReapPausedDirectPlay);
        Assert.Equal(7, config.LoginNagThreshold);
    }
}
