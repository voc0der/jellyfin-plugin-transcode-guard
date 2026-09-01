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
