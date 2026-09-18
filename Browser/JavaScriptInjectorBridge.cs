using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace Jellyfin.Plugin.TranscodeGuard.Browser;

internal enum InjectorRegistrationStatus
{
    Registered,
    NotInstalled,
    Failed
}

internal readonly record struct InjectorRegistrationResult(InjectorRegistrationStatus Status, string? Detail)
{
    internal static InjectorRegistrationResult Registered { get; } = new(InjectorRegistrationStatus.Registered, null);

    internal static InjectorRegistrationResult NotInstalled { get; } = new(InjectorRegistrationStatus.NotInstalled, null);

    internal static InjectorRegistrationResult Failed(string detail) => new(InjectorRegistrationStatus.Failed, detail);
}

/// <summary>
/// A script registration in the shape JavaScript Injector's <c>PluginInterface.RegisterScript</c> expects.
/// </summary>
internal sealed record InjectorScript(
    string Id,
    string Name,
    string Script,
    bool RequiresAuthentication,
    string PluginId,
    string PluginName,
    string PluginVersion)
{
    internal string ToJson() => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["id"] = Id,
        ["name"] = Name,
        ["script"] = Script,
        ["enabled"] = true,
        ["requiresAuthentication"] = RequiresAuthentication,
        ["pluginId"] = PluginId,
        ["pluginName"] = PluginName,
        ["pluginVersion"] = PluginVersion
    });
}

/// <summary>
/// Talks to the optional JavaScript Injector plugin through reflection, so Transcode Guard neither
/// ships it nor fails to load without it.
/// </summary>
internal sealed class JavaScriptInjectorBridge
{
    internal const string InjectorAssemblyName = "Jellyfin.Plugin.JavaScriptInjector";
    internal const string PluginInterfaceTypeName = "Jellyfin.Plugin.JavaScriptInjector.PluginInterface";

    private readonly Func<Type?> _locatePluginInterface;

    public JavaScriptInjectorBridge()
        : this(LocatePluginInterface)
    {
    }

    internal JavaScriptInjectorBridge(Func<Type?> locatePluginInterface)
    {
        _locatePluginInterface = locatePluginInterface ?? throw new ArgumentNullException(nameof(locatePluginInterface));
    }

    /// <summary>
    /// Finds the injector's public <c>PluginInterface</c>. Jellyfin gives every plugin its own load
    /// context, so the injector is looked up by name across all of them rather than referenced.
    /// </summary>
    /// <returns>The interface type, or null when the injector is not loaded.</returns>
    internal static Type? LocatePluginInterface()
    {
        foreach (var context in AssemblyLoadContext.All)
        {
            foreach (var assembly in context.Assemblies)
            {
                if (!string.Equals(assembly.GetName().Name, InjectorAssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var pluginInterface = assembly.GetType(PluginInterfaceTypeName, throwOnError: false);
                if (pluginInterface != null)
                {
                    return pluginInterface;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Registers or replaces a script. Never throws: the injector is optional, and a failure here
    /// only means browsers keep getting the normal popup.
    /// </summary>
    /// <param name="script">The script to register.</param>
    /// <returns>What happened.</returns>
    internal InjectorRegistrationResult Register(InjectorScript script)
    {
        ArgumentNullException.ThrowIfNull(script);

        try
        {
            var pluginInterface = _locatePluginInterface();
            if (pluginInterface == null)
            {
                return InjectorRegistrationResult.NotInstalled;
            }

            var register = pluginInterface.GetMethod("RegisterScript", BindingFlags.Public | BindingFlags.Static);
            if (register == null || register.GetParameters() is not [var payloadParameter])
            {
                return InjectorRegistrationResult.Failed("PluginInterface.RegisterScript(payload) was not found");
            }

            // RegisterScript takes the injector's own Newtonsoft JObject. Building it with that
            // parameter type's Parse method keeps Newtonsoft out of this plugin and guarantees the
            // payload is the exact type the injector's load context expects.
            var parse = payloadParameter.ParameterType.GetMethod(
                "Parse",
                BindingFlags.Public | BindingFlags.Static,
                new[] { typeof(string) });
            if (parse == null)
            {
                return InjectorRegistrationResult.Failed(
                    $"{payloadParameter.ParameterType.FullName}.Parse(string) was not found");
            }

            var payload = parse.Invoke(null, new object[] { script.ToJson() });
            var result = register.Invoke(null, new[] { payload });

            return result is true
                ? InjectorRegistrationResult.Registered
                : InjectorRegistrationResult.Failed("RegisterScript returned false");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null and not OutOfMemoryException)
        {
            return InjectorRegistrationResult.Failed(Describe(ex.InnerException));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return InjectorRegistrationResult.Failed(Describe(ex));
        }
    }

    /// <summary>
    /// Removes a script. Never throws.
    /// </summary>
    /// <param name="scriptId">The script's ID.</param>
    /// <returns>True when the injector reported the script removed.</returns>
    internal bool Unregister(string scriptId)
    {
        try
        {
            var unregister = _locatePluginInterface()?.GetMethod(
                "UnregisterScript",
                BindingFlags.Public | BindingFlags.Static,
                new[] { typeof(string) });

            return unregister?.Invoke(null, new object[] { scriptId }) is true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;
}
