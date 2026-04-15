using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Json;

namespace Dmart.Plugins.Native;

// Scans ~/.dmart/plugins/<name>/ directories for native .so plugins and their
// config.json files. Creates IHookPlugin/IApiPlugin adapter instances that
// PluginManager dispatches to identically to built-in plugins.
public static class NativePluginLoader
{
    public static void AddNativePlugins(this IServiceCollection services)
    {
        var customRoot = FindPluginsRoot();
        if (customRoot is null) return;

        foreach (var dir in Directory.EnumerateDirectories(customRoot))
        {
            var dirName = Path.GetFileName(dir);
            var configPath = Path.Combine(dir, "config.json");
            if (!File.Exists(configPath)) continue;

            var soPath = FindSharedLibrary(dir, dirName);
            if (soPath is null) continue;

            try
            {
                var handle = NativePluginHandle.Load(soPath);
                var infoJson = handle.CallGetInfo();
                using var infoDoc = JsonDocument.Parse(infoJson);
                var root = infoDoc.RootElement;

                var shortname = root.TryGetProperty("shortname", out var sn)
                    ? sn.GetString() ?? dirName : dirName;
                var typeStr = root.TryGetProperty("type", out var tp)
                    ? tp.GetString() ?? "hook" : "hook";

                if (typeStr == "hook")
                {
                    if (handle.Hook is null)
                    {
                        Console.WriteLine($"NATIVE_PLUGIN_ERROR: {shortname} type=hook but no hook() export");
                        handle.Dispose();
                        continue;
                    }
                    services.AddSingleton<IHookPlugin>(new NativeHookPlugin(handle, shortname));
                    Console.WriteLine($"NATIVE_PLUGIN_REGISTERED: {shortname} (hook) from {soPath}");
                }
                else if (typeStr == "api")
                {
                    if (handle.HandleRequest is null)
                    {
                        Console.WriteLine($"NATIVE_PLUGIN_ERROR: {shortname} type=api but no handle_request() export");
                        handle.Dispose();
                        continue;
                    }
                    var routes = ParseRoutes(root);
                    services.AddSingleton<IApiPlugin>(new NativeApiPlugin(handle, shortname, routes));
                    Console.WriteLine($"NATIVE_PLUGIN_REGISTERED: {shortname} (api, {routes.Count} routes) from {soPath}");
                }
                else
                {
                    Console.WriteLine($"NATIVE_PLUGIN_ERROR: {shortname} unknown type '{typeStr}'");
                    handle.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"NATIVE_PLUGIN_LOAD_FAILED: {dirName}: {ex.Message}");
            }
        }
    }

    // Also loads config.json files from the native plugins directory so
    // PluginManager can register them in its dispatch tables.
    public static List<PluginWrapper> LoadNativeConfigs()
    {
        var configs = new List<PluginWrapper>();
        var customRoot = FindPluginsRoot();
        if (customRoot is null) return configs;

        foreach (var dir in Directory.EnumerateDirectories(customRoot))
        {
            var configPath = Path.Combine(dir, "config.json");
            if (!File.Exists(configPath)) continue;

            try
            {
                var json = File.ReadAllText(configPath);
                var wrapper = JsonSerializer.Deserialize(json, DmartJsonContext.Default.PluginWrapper);
                if (wrapper is not null)
                {
                    wrapper.Shortname = Path.GetFileName(dir);
                    configs.Add(wrapper);
                }
            }
            catch { /* skip malformed configs */ }
        }

        return configs;
    }

    private static string? FindPluginsRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var homePath = Path.Combine(home, ".dmart", "plugins");
            if (Directory.Exists(homePath)) return homePath;
        }
        return null;
    }

    internal static string? FindSharedLibrary(string dir, string dirName)
    {
        var simple = Path.Combine(dir, $"{dirName}.so");
        if (File.Exists(simple)) return simple;

        var lib = Path.Combine(dir, $"lib{dirName}.so");
        if (File.Exists(lib)) return lib;

        foreach (var ext in new[] { "*.so", "*.dylib", "*.dll" })
        {
            var files = Directory.GetFiles(dir, ext);
            if (files.Length > 0) return files[0];
        }

        return null;
    }

    private static List<NativeApiPlugin.NativeRoute> ParseRoutes(JsonElement root)
    {
        var routes = new List<NativeApiPlugin.NativeRoute>();
        if (root.TryGetProperty("routes", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in arr.EnumerateArray())
            {
                var method = r.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET";
                var path = r.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/";
                routes.Add(new NativeApiPlugin.NativeRoute(method, path));
            }
        }
        return routes;
    }
}
