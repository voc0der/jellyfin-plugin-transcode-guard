using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeGuard.Browser;

/// <summary>
/// Keeps the browser script registered with JavaScript Injector while the browser install warning
/// is switched on.
/// </summary>
/// <remarks>
/// Registration happens at startup and when the setting changes, never per playback. Shutdown
/// leaves the registration in place so the injector's configuration is not rewritten on every
/// restart. A registration left behind while the feature is off is harmless: the script only
/// reacts to nags carrying the warning, and none are sent while it is off.
/// </remarks>
public sealed class BrowserScriptRegistrar : IHostedService
{
    internal const string ScriptResourceName = "Jellyfin.Plugin.TranscodeGuard.Browser.TranscodeGuardBrowser.js";
    internal const string ScriptName = "Transcode Guard Browser Install Warning";

    private readonly JavaScriptInjectorBridge _bridge;
    private readonly Func<string> _scriptLoader;
    private readonly ILogger<BrowserScriptRegistrar> _logger;
    private readonly object _lock = new();
    private Plugin? _plugin;
    private bool _registered;

    public BrowserScriptRegistrar(ILogger<BrowserScriptRegistrar> logger)
        : this(new JavaScriptInjectorBridge(), LoadScript, logger)
    {
    }

    internal BrowserScriptRegistrar(
        JavaScriptInjectorBridge bridge,
        Func<string> scriptLoader,
        ILogger<BrowserScriptRegistrar> logger)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _scriptLoader = scriptLoader ?? throw new ArgumentNullException(nameof(scriptLoader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the script's ID in JavaScript Injector. Stable across restarts, so registering again
    /// replaces the stored script instead of adding a copy.
    /// </summary>
    internal static string ScriptId { get; } = Plugin.PluginId.ToString("D") + "-browser-client";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _plugin = Plugin.Instance;
        if (_plugin == null)
        {
            return Task.CompletedTask;
        }

        _plugin.ConfigurationChanged += OnConfigurationChanged;
        Apply(_plugin.Configuration);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_plugin != null)
        {
            _plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers the script when the warning is on and not yet registered, and removes it when the
    /// warning is switched off. A failed registration is retried on the next configuration change.
    /// </summary>
    /// <param name="config">The current configuration.</param>
    internal void Apply(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_lock)
        {
            if (!config.EnableBrowserInstallNag)
            {
                if (_registered)
                {
                    _bridge.Unregister(ScriptId);
                    _registered = false;
                    _logger.LogInformation("Browser install warning switched off; removed its script from JavaScript Injector");
                }

                return;
            }

            if (!string.IsNullOrWhiteSpace(config.BrowserInstallUrl)
                && BrowserNagRules.NormalizeInstallUrl(config.BrowserInstallUrl) == null)
            {
                _logger.LogWarning(
                    "Browser install warning URL {InstallUrl} is not an absolute http or https link, so the warning is shown without an install button",
                    config.BrowserInstallUrl);
            }

            if (_registered)
            {
                return;
            }

            // Registering at every startup also replaces the stored script after a plugin upgrade.
            var result = Register();
            switch (result.Status)
            {
                case InjectorRegistrationStatus.Registered:
                    _registered = true;
                    _logger.LogInformation(
                        "JavaScript Injector detected; registered the browser install warning script ({ScriptId})",
                        ScriptId);
                    break;
                case InjectorRegistrationStatus.NotInstalled:
                    _logger.LogWarning(
                        "Browser install warning is enabled, but the Jellyfin JavaScript Injector plugin was not found. Jellyfin Web sessions get the normal Transcode Guard message instead.");
                    break;
                default:
                    _logger.LogWarning(
                        "Browser install warning is enabled, but registering its script with JavaScript Injector failed ({Reason}). Jellyfin Web sessions get the normal Transcode Guard message instead.",
                        result.Detail);
                    break;
            }
        }
    }

    private InjectorRegistrationResult Register()
    {
        string script;
        try
        {
            script = _scriptLoader();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return InjectorRegistrationResult.Failed("the embedded browser script could not be read: " + ex.Message);
        }

        return _bridge.Register(new InjectorScript(
            ScriptId,
            ScriptName,
            script,
            RequiresAuthentication: true,
            Plugin.PluginId.ToString("D"),
            Plugin.Instance?.Name ?? "Transcode Guard",
            Plugin.Instance?.Version.ToString() ?? typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty));
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
    {
        // Saving the settings page must never fail because the optional injector misbehaved.
        try
        {
            if (configuration is PluginConfiguration config)
            {
                Apply(config);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error updating the browser install warning script registration");
        }
    }

    internal static string LoadScript()
    {
        using var stream = typeof(BrowserScriptRegistrar).Assembly.GetManifestResourceStream(ScriptResourceName)
            ?? throw new InvalidOperationException("Embedded resource " + ScriptResourceName + " is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
