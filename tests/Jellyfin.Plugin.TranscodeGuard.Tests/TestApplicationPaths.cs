using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

internal sealed class TestApplicationPaths : IApplicationPaths
{
    public TestApplicationPaths(string rootPath)
    {
        ProgramDataPath = rootPath;
        WebPath = Path.Combine(rootPath, "web");
        ProgramSystemPath = Path.Combine(rootPath, "system");
        DataPath = Path.Combine(rootPath, "data");
        ImageCachePath = Path.Combine(rootPath, "image-cache");
        PluginsPath = Path.Combine(rootPath, "plugins");
        PluginConfigurationsPath = Path.Combine(rootPath, "plugin-configs");
        LogDirectoryPath = Path.Combine(rootPath, "logs");
        ConfigurationDirectoryPath = Path.Combine(rootPath, "config");
        SystemConfigurationFilePath = Path.Combine(rootPath, "config", "system.xml");
        CachePath = Path.Combine(rootPath, "cache");
        TempDirectory = Path.Combine(rootPath, "temp");
        VirtualDataPath = Path.Combine(rootPath, "virtual-data");
    }

    public string ProgramDataPath { get; }

    public string WebPath { get; }

    public string ProgramSystemPath { get; }

    public string DataPath { get; }

    public string ImageCachePath { get; }

    public string PluginsPath { get; }

    public string PluginConfigurationsPath { get; }

    public string LogDirectoryPath { get; }

    public string ConfigurationDirectoryPath { get; }

    public string SystemConfigurationFilePath { get; }

    public string CachePath { get; }

    public string TempDirectory { get; }

    public string VirtualDataPath { get; }
}
