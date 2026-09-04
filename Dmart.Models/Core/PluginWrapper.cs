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
// Accessors are `set`, not `init`, and that is load-bearing rather than a style
// choice. With any init-only property the source-generated deserializer stops
// using the parameterless constructor and switches to
// `ObjectWithParameterizedConstructorCreator`, which assigns EVERY such
// property from an args array — passing `default(T)` for anything the JSON
// omitted. The constructor still runs, so the initialisers below execute and
// are then immediately overwritten with 0 / false / null.
//
// That silently inverted two documented defaults for every plugin whose
// config.json left them out: `ordinal` became 0 rather than 9999 (so plugins
// sorted first instead of last) and `concurrent` became false rather than true
// (so after-hooks were awaited instead of fire-and-forget). `dependencies`
// arrived as null rather than an empty list.
//
// PluginWrapperDefaultsTests pins the behaviour. If a property here ever needs
// to become init-only again, give it an explicit [JsonConstructor] with real
// parameter defaults instead — the initialiser alone will not survive.
public sealed record PluginWrapper
{
    public string Shortname { get; set; } = "";
    public bool IsActive { get; set; }
    public EventFilter? Filters { get; set; }
    public EventListenTime? ListenTime { get; set; }
    public PluginType? Type { get; set; }
    public int Ordinal { get; set; } = 9999;
    public List<string> Dependencies { get; set; } = new();
    public bool Concurrent { get; set; } = true;
    public Dictionary<string, object>? Attributes { get; set; }
}
