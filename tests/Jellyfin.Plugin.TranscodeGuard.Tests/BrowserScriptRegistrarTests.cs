using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.TranscodeGuard.Browser;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

public class BrowserScriptRegistrarTests
{
    [Fact]
    public void ScriptId_IsStableAndDerivedFromThePluginGuid()
    {
        Assert.Equal("2ce6b237-2610-4dfd-a34b-3f4c2ac64a88-browser-client", BrowserScriptRegistrar.ScriptId);
    }

    [Fact]
    public void Apply_LeavesTheInjectorAloneWhileTheFeatureIsOff()
    {
        var injector = new CountingInjector();
        var registrar = injector.CreateRegistrar(new ListLogger<BrowserScriptRegistrar>());

        registrar.Apply(new PluginConfiguration());

        Assert.Equal(0, injector.RegisterCalls);
        Assert.Equal(0, injector.UnregisterCalls);
    }

    [Fact]
    public void Apply_RegistersOnceWhileTheFeatureStaysOn()
    {
        var injector = new CountingInjector();
        var registrar = injector.CreateRegistrar(new ListLogger<BrowserScriptRegistrar>());
        var config = new PluginConfiguration { EnableBrowserInstallNag = true };

        registrar.Apply(config);
        registrar.Apply(config);

        Assert.Equal(1, injector.RegisterCalls);
        using var payload = JsonDocument.Parse(injector.LastPayloadJson!);
        Assert.Equal(BrowserScriptRegistrar.ScriptId, payload.RootElement.GetProperty("id").GetString());
        Assert.True(payload.RootElement.GetProperty("requiresAuthentication").GetBoolean());
        Assert.Equal("script body", payload.RootElement.GetProperty("script").GetString());
    }

    [Fact]
    public void Apply_RemovesTheScriptWhenTheFeatureIsSwitchedOff()
    {
        var injector = new CountingInjector();
        var registrar = injector.CreateRegistrar(new ListLogger<BrowserScriptRegistrar>());

        registrar.Apply(new PluginConfiguration { EnableBrowserInstallNag = true });
        registrar.Apply(new PluginConfiguration());

        Assert.Equal(1, injector.UnregisterCalls);
        Assert.Equal(BrowserScriptRegistrar.ScriptId, injector.LastUnregisteredId);
    }

    [Fact]
    public void Apply_WarnsWhenTheInjectorIsMissingAndTriesAgainOnTheNextChange()
    {
        var logger = new ListLogger<BrowserScriptRegistrar>();
        var locateCalls = 0;
        var bridge = new JavaScriptInjectorBridge(() =>
        {
            locateCalls++;
            return null;
        });
        var registrar = new BrowserScriptRegistrar(bridge, () => "script body", logger);
        var config = new PluginConfiguration { EnableBrowserInstallNag = true };

        registrar.Apply(config);
        registrar.Apply(config);

        Assert.Equal(2, locateCalls);
        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("JavaScript Injector plugin was not found", StringComparison.Ordinal)));
    }

    [Fact]
    public void Apply_WarnsAboutAnInstallUrlThatWillNotBeShown()
    {
        var logger = new ListLogger<BrowserScriptRegistrar>();
        var registrar = new CountingInjector().CreateRegistrar(logger);

        registrar.Apply(new PluginConfiguration { EnableBrowserInstallNag = true, BrowserInstallUrl = "javascript:alert(1)" });

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("without an install button", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_ReportsAnUnreadableScriptInsteadOfThrowing()
    {
        var logger = new ListLogger<BrowserScriptRegistrar>();
        var injector = new CountingInjector();
        var registrar = new BrowserScriptRegistrar(
            new JavaScriptInjectorBridge(() => typeof(CountingInjector.Interface)),
            () => throw new IOException("gone"),
            logger);

        registrar.Apply(new PluginConfiguration { EnableBrowserInstallNag = true });

        Assert.Equal(0, injector.RegisterCalls);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("gone", StringComparison.Ordinal));
    }

    [Fact]
    public void EmbeddedScript_ReadsEveryArgumentTheServerSends()
    {
        var script = BrowserScriptRegistrar.LoadScript();

        var argumentNames = typeof(BrowserNagRules)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.Name.EndsWith("Argument", StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(argumentNames);
        Assert.All(argumentNames, name => Assert.Contains("'" + name + "'", script, StringComparison.Ordinal));
        Assert.Contains("MARKER_VALUE = '" + BrowserNagRules.MarkerValue + "'", script, StringComparison.Ordinal);
        Assert.Contains("MIN_AUTO_CLOSE_SECONDS = " + BrowserNagRules.MinAutoCloseSeconds + ";", script, StringComparison.Ordinal);
        Assert.Contains("MAX_AUTO_CLOSE_SECONDS = " + BrowserNagRules.MaxAutoCloseSeconds + ";", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedScript_StaysInEs5Syntax()
    {
        // JavaScript Injector serves every plugin's script as one file, so syntax an older TV
        // browser cannot parse would take the other plugins' scripts down with this one.
        var script = BrowserScriptRegistrar.LoadScript();

        Assert.DoesNotContain("=>", script, StringComparison.Ordinal);
        Assert.DoesNotContain("`", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"(?<![\w$-])(let|const|class|async|await)\s+[\w$]"), script);
    }

    private sealed class CountingInjector
    {
        public int RegisterCalls => Interface.RegisterCalls;

        public int UnregisterCalls => Interface.UnregisterCalls;

        public string? LastPayloadJson => Interface.LastPayloadJson;

        public string? LastUnregisteredId => Interface.LastUnregisteredId;

        public CountingInjector()
        {
            Interface.Reset();
        }

        public BrowserScriptRegistrar CreateRegistrar(ILogger<BrowserScriptRegistrar> logger)
            => new(new JavaScriptInjectorBridge(() => typeof(Interface)), () => "script body", logger);

        /// <summary>
        /// Static like the real PluginInterface. Only this test class uses it, and xUnit runs a
        /// class's tests one at a time.
        /// </summary>
        public static class Interface
        {
            public static int RegisterCalls { get; private set; }

            public static int UnregisterCalls { get; private set; }

            public static string? LastPayloadJson { get; private set; }

            public static string? LastUnregisteredId { get; private set; }

            public static void Reset()
            {
                RegisterCalls = 0;
                UnregisterCalls = 0;
                LastPayloadJson = null;
                LastUnregisteredId = null;
            }

            public static bool RegisterScript(JavaScriptInjectorBridgeTests.FakePayload payload)
            {
                RegisterCalls++;
                LastPayloadJson = payload.Json;
                return true;
            }

            public static bool UnregisterScript(string scriptId)
            {
                UnregisterCalls++;
                LastUnregisteredId = scriptId;
                return true;
            }
        }
    }
}
