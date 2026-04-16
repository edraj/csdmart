using System.Reflection;
using Dmart.Models.Api;
using Dmart.Plugins;

namespace Dmart.Api.Info;

public static class ManifestHandler
{
    private static readonly string Version = ResolveVersion();

    private static string ResolveVersion()
    {
        var asm = typeof(ManifestHandler).Assembly
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        if (!string.IsNullOrEmpty(asm) && asm.Contains("branch="))
            return asm.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return asm ?? "dev";
    }

    public static void Map(RouteGroupBuilder g) =>
        g.MapGet("/manifest", (PluginManager plugins) => Response.Ok(attributes: new()
        {
            ["name"] = "dmart",
            ["version"] = Version,
            ["api"] = "v1",
            // Matches dmart Python's /info/manifest — the list of shortnames
            // currently loaded by PluginManager after filtering on is_active.
            ["plugins"] = plugins.ActivePlugins.ToList(),
        }));
}
