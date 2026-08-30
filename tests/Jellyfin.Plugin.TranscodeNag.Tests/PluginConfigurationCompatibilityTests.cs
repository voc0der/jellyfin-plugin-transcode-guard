using System.Xml.Serialization;
using Jellyfin.Plugin.TranscodeNag.Configuration;

namespace Jellyfin.Plugin.TranscodeNag.Tests;

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
}
