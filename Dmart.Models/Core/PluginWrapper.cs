using Dmart.Models.Enums;

namespace Dmart.Models.Core;

// Wire shape of a plugin's config.json on disk. The "object" field from
// Python (the actual plugin instance) is not part of the wire form —
// PluginManager attaches the C# instance at load time via a separate lookup.
//
// Activation:
//   - `is_active`   — is the plugin loaded at all?
//   - `filters`     — for hook plugins, declares the (space × subpath ×
//                     resource_type × schema × action) scope this plugin
//                     fires on. Same vocabulary the permission engine uses
//                     (see EventFilter.cs). The plugin author owns scope —
//                     spaces no longer carry an `active_plugins` opt-in
//                     list (removed in favor of self-declared filters).
public sealed record PluginWrapper
{
    public string Shortname { get; set; } = "";
    public bool IsActive { get; init; }
    public EventFilter? Filters { get; init; }
    public EventListenTime? ListenTime { get; init; }
    public PluginType? Type { get; init; }
    public int Ordinal { get; init; } = 9999;
    public List<string> Dependencies { get; init; } = new();
    public bool Concurrent { get; init; } = true;

    // How many worker processes to run for an external plugin. Exchanges are
    // serialized per worker because the line protocol has no request ids, so
    // this is the only knob that lets a plugin handle calls in parallel.
    //
    // Default 1 — one process, one call at a time, which is what every plugin
    // written before this existed assumes. Raising it makes any state the
    // plugin keeps between calls per-worker rather than per-plugin, so it is
    // opt-in rather than sized automatically. Ignored by built-in plugins.
    public int Workers { get; init; } = 1;
    public Dictionary<string, object>? Attributes { get; init; }
}
