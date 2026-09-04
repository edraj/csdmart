using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Json;

namespace Dmart.Plugins.Native;

// Scans ~/.dmart/plugins/<name>/ directories for external plugins.
//
// A plugin is a standalone executable that reads JSON lines from stdin and
// writes them to stdout; if it crashes, dmart respawns it on the next event.
// The adapters it gets (SubprocessHookPlugin / SubprocessApiPlugin) implement
// IHookPlugin/IApiPlugin, so PluginManager dispatches to them identically to
// built-in plugins.
//
// dmart also loaded in-process `.so` plugins through NativeLibrary.Load until
// they were removed: a segfault in third-party code took the host down with
// it, and dlopen does not work in a static build at all. A directory holding
// only a `.so` is now reported as a load failure naming the removal, rather
// than silently doing nothing.
public static class NativePluginLoader
{
    // Every SubprocessPluginHost we spawn. Walked on ApplicationStopping so
    // each subprocess gets a clean stdin-close (EOF) shutdown before the
    // dotnet process exits. Without this, subprocesses only find out dmart
    // is gone when their next stdin write raises a broken-pipe.
    private static readonly List<SubprocessPluginHost> _hosts = new();

    // Every plugin directory that failed to load, and why.
    //
    // The scan runs during DI registration, before the host — and therefore the
    // logger — exists, so failures could only go to stderr. One line on stderr
    // among a startup's worth of output is easy to miss, and dmart carried on
    // as if nothing were wrong: a deployment that lost its plugins looked
    // completely healthy. Collected here so Program.cs can log them through the
    // real pipeline once it is up, and so /info/plugins can report them.
    private static readonly List<PluginLoadFailure> _failures = new();

    public static IReadOnlyList<PluginLoadFailure> LoadFailures => _failures;

    private static void RecordFailure(
        List<PluginLoadFailure> failures, string dirName, string reason)
    {
        failures.Add(new PluginLoadFailure(dirName, reason));
        Console.Error.WriteLine($"NATIVE_PLUGIN_LOAD_FAILED: {dirName}: {reason}");
    }

    public static void AddNativePlugins(this IServiceCollection services)
    {
        var customRoot = FindPluginsRoot();
        if (customRoot is null) return;
        _failures.AddRange(ScanRoot(services, customRoot));
    }

    // Split from AddNativePlugins so the scan can be exercised against a
    // temporary directory. FindPluginsRoot is hard-wired to $HOME/.dmart/plugins,
    // and a test that moves $HOME to reach it would be far more fragile than the
    // behaviour it is checking.
    internal static List<PluginLoadFailure> ScanRoot(
        IServiceCollection services, string customRoot)
    {
        var failures = new List<PluginLoadFailure>();
        foreach (var dir in Directory.EnumerateDirectories(customRoot))
        {
            var dirName = Path.GetFileName(dir);
            var configPath = Path.Combine(dir, "config.json");
            if (!File.Exists(configPath)) continue;

            var execPath = FindExecutable(dir, dirName);
            if (execPath is not null)
            {
                LoadSubprocessPlugin(services, execPath, dirName, failures);
                continue;
            }

            // config.json present but no executable. Previously the loop just
            // fell through in silence, which is the easiest failure of all to
            // ship: the operator wrote a config, the binary is missing or
            // misnamed, and nothing anywhere says so.
            //
            // A leftover .so gets its own message. That deployment worked
            // before in-process plugins were removed, so "no plugin binary
            // found" would be actively misleading — the binary is right there.
            RecordFailure(failures, dirName, FindSharedLibrary(dir, dirName) is { } soPath
                ? $"found {Path.GetFileName(soPath)} but in-process .so plugins are no longer "
                  + "supported — port the plugin to a subprocess executable "
                  + "(see custom_plugins_sdk/README.md)"
                : $"config.json present but no plugin executable found in {dir}");
        }
        return failures;
    }

    private static void LoadSubprocessPlugin(IServiceCollection services, string execPath, string dirName, List<PluginLoadFailure> failures)
    {
        try
        {
            var host = new SubprocessPluginHost(execPath, dirName);
            _hosts.Add(host);

            // Ask the plugin for its info
            // The "host" object advertises what this dmart can do for the
            // plugin. A plugin built against a newer SDK can then avoid sending
            // a callback an older host would never answer — which would strand
            // it waiting while the host read the frame as its final response.
            var infoJson = host.SendAndReceive("{\"type\":\"info\",\"host\":{\"callbacks\":1}}");
            using var infoDoc = JsonDocument.Parse(infoJson);
            var root = infoDoc.RootElement;

            var shortname = root.TryGetProperty("shortname", out var sn)
                ? sn.GetString() ?? dirName : dirName;
            var typeStr = root.TryGetProperty("type", out var tp)
                ? tp.GetString() ?? "hook" : "hook";
            // Optional version, baked into the subprocess binary by its own
            // build pipeline (e.g. Go ldflags, Python __version__, npm
            // package.json read at startup) and surfaced via the info channel.
            var version = root.TryGetProperty("version", out var ver)
                ? (ver.GetString() ?? "0.0.0") : "0.0.0";

            if (typeStr == "hook")
            {
                services.AddSingleton<IHookPlugin>(new SubprocessHookPlugin(host, version));
                Console.Error.WriteLine($"SUBPROCESS_PLUGIN_REGISTERED: {shortname} v{version} (hook) from {execPath}");
            }
            else if (typeStr == "api")
            {
                var routes = ParseRoutes(root);
                services.AddSingleton<IApiPlugin>(new SubprocessApiPlugin(host, routes, version));
                Console.Error.WriteLine($"SUBPROCESS_PLUGIN_REGISTERED: {shortname} v{version} (api, {routes.Count} routes) from {execPath}");
            }
            else
            {
                RecordFailure(failures, dirName, $"unknown plugin type '{typeStr}'");
                host.Dispose();
            }
        }
        catch (Exception ex)
        {
            RecordFailure(failures, dirName, $"subprocess load failed: {ex.Message}");
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
                    // Gated to avoid leaking config.json contents (potential
                    // secrets) on every startup. Set DMART_DEBUG_PLUGIN_CONFIG=1
                    // when debugging plugin load. Parallel to the LogDebug
                    // path in PluginManager, but native loads happen during
                    // DI build before the logger is wired up — hence stderr.
                    if (Environment.GetEnvironmentVariable("DMART_DEBUG_PLUGIN_CONFIG") == "1")
                        Console.Error.WriteLine(
                            $"PLUGIN_CONFIG: {wrapper.Shortname} from {configPath} {JsonUtil.Compact(json)}");
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

    // Find an executable file (not a library) to run as the plugin.
    internal static string? FindExecutable(string dir, string dirName)
    {
        // Try <dirname> (exact name, no extension)
        var exact = Path.Combine(dir, dirName);
        if (File.Exists(exact) && IsExecutable(exact)) return exact;

        // Try any file without a common library extension that is executable
        foreach (var file in Directory.GetFiles(dir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".json" or ".so" or ".dylib" or ".dll" or ".dbg" or ".pdb" or ".md" or ".txt")
                continue;
            if (IsExecutable(file)) return file;
        }

        return null;
    }

    // Retained only to recognise a pre-removal deployment so ScanRoot can say
    // so explicitly. Nothing loads the file it finds.
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

    private static bool IsExecutable(string path)
    {
        try
        {
            // Check Unix executable bit
            var info = new FileInfo(path);
            return info.Exists && (info.UnixFileMode & UnixFileMode.UserExecute) != 0;
        }
        catch { return false; }
    }

    // Invoke from Program.cs once the WebApplication has been built, so we
    // can register a graceful-shutdown callback on IHostApplicationLifetime.
    // For subprocess plugins, this sends an EOF on their stdin so they exit
    // cleanly on the next read rather than learning about shutdown via a
    // broken-pipe (or tripping a KeyboardInterrupt on terminal Ctrl+C).
    public static void WireSubprocessShutdown(IHostApplicationLifetime lifetime)
    {
        lifetime.ApplicationStopping.Register(() =>
        {
            foreach (var h in _hosts)
            {
                try { h.Shutdown(); } catch { /* best-effort */ }
            }
        });
    }

    private static List<NativeRoute> ParseRoutes(JsonElement root)
    {
        var routes = new List<NativeRoute>();
        if (root.TryGetProperty("routes", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in arr.EnumerateArray())
            {
                var method = r.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET";
                var path = r.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/";
                routes.Add(new NativeRoute(method, path));
            }
        }
        return routes;
    }
}

// A plugin directory that was found but could not be loaded. Surfaced by
// /info/plugins and logged at startup so a missing plugin is visible rather
// than inferred from behaviour that quietly stopped happening.
public sealed record PluginLoadFailure(string Shortname, string Reason);
