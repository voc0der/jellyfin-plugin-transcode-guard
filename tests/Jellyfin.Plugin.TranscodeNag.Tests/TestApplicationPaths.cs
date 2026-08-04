using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.TranscodeNag.Tests;

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
        TrickplayPath = Path.Combine(rootPath, "trickplay");
        BackupPath = Path.Combine(rootPath, "backups");
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

    public string TrickplayPath { get; }

    public string BackupPath { get; }

    public void MakeSanityCheckOrThrow()
    {
        throw new NotSupportedException();
    }

    public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
    {
        throw new NotSupportedException();
    }
}
