using System.Xml.Serialization;
using Jellyfin.Plugin.TranscodeNag.Configuration;

namespace Jellyfin.Plugin.TranscodeNag.Tests;

public class PluginConfigurationCompatibilityTests
{
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

        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        Assert.DoesNotContain("MinimumFreeGpuMemoryMiB", writer.ToString(), StringComparison.Ordinal);
    }
}
