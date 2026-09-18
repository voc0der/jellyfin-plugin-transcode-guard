using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TranscodeGuard.Browser;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TranscodeGuard;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Transcode Guard";

    public override Guid Id => PluginId;

    public override string Description => "Nags users when they transcode due to unsupported formats (but allows bitrate transcoding)";

    public static Plugin? Instance { get; private set; }

    internal static Guid PluginId { get; } = Guid.Parse("2ce6b237-2610-4dfd-a34b-3f4c2ac64a88");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }

    public override void OnUninstalling()
    {
        // Take the browser script out of JavaScript Injector with the plugin; the injector
        // outlives it and would otherwise keep serving a script nothing drives any more.
        new JavaScriptInjectorBridge().Unregister(BrowserScriptRegistrar.ScriptId);

        base.OnUninstalling();
    }
}
