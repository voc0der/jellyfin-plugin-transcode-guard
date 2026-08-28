using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.TranscodeNag.Gpu;
using Jellyfin.Plugin.TranscodeNag.Messaging;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeNag;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Gets why the GPU guard could not be installed, or null when it was.
    /// Read once by <see cref="PlaybackMonitor"/> at startup.
    /// </summary>
    internal static string? DecorationFailure { get; private set; }

    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IClientMessageService, ClientMessageService>();
        serviceCollection.AddSingleton<IGpuMemoryProvider, NvidiaSmiGpuMemoryProvider>();
        serviceCollection.AddSingleton<GpuResourceGuard>();
        serviceCollection.AddHostedService<PlaybackMonitor>();

        TryDecorateTranscodeManager(serviceCollection);
    }

    /// <summary>
    /// Wraps Jellyfin's <c>ITranscodeManager</c> so the GPU guard can refuse a hardware transcode
    /// before FFmpeg is launched.
    /// </summary>
    /// <remarks>
    /// Jellyfin calls plugin service registrators after its own <c>RegisterServices</c>, so the core
    /// descriptor is already in the collection and can be replaced by one that decorates it. The
    /// call is isolated behind a non-inlined method and a broad catch because <c>ITranscodeManager</c>
    /// does not exist before Jellyfin 10.10 - on an older server the type load must degrade to
    /// "no guard", not to a malfunctioning plugin that also loses the nag features.
    /// </remarks>
    internal static void TryDecorateTranscodeManager(IServiceCollection serviceCollection)
    {
        DecorationFailure = null;

        try
        {
            DecorateTranscodeManager(serviceCollection);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Losing the GPU guard must not cost the user the nag and MOTD features: Jellyfin
            // marks a plugin malfunctioned if a registrator throws. PlaybackMonitor reports this
            // on startup so the degradation is visible in the log rather than silent.
            DecorationFailure = ex.GetType().Name + ": " + ex.Message;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DecorateTranscodeManager(IServiceCollection serviceCollection)
    {
        var descriptor = serviceCollection.LastOrDefault(service => service.ServiceType == typeof(ITranscodeManager));

        // Without a concrete implementation type there is no safe way to rebuild the inner manager,
        // so the guard stands down rather than guessing.
        if (descriptor == null)
        {
            DecorationFailure = "no ITranscodeManager registration was found";
            return;
        }

        if (descriptor.ImplementationType == null)
        {
            DecorationFailure = "the ITranscodeManager registration is not a concrete implementation type";
            return;
        }

        var implementationType = descriptor.ImplementationType;
        var lifetime = descriptor.Lifetime;

        var replacement = new ServiceDescriptor(
            typeof(ITranscodeManager),
            provider => new GuardedTranscodeManager(
                (ITranscodeManager)ActivatorUtilities.CreateInstance(provider, implementationType),
                provider.GetRequiredService<GpuResourceGuard>(),
                provider.GetRequiredService<ILogger<GuardedTranscodeManager>>()),
            lifetime);

        // Swap without an intermediate state in which nothing provides ITranscodeManager.
        serviceCollection.Remove(descriptor);
        serviceCollection.Add(replacement);
    }
}
