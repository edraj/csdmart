using Dmart.Plugins;

namespace Dmart.Services;

public sealed class PluginHost(IEnumerable<IPlugin> plugins, ILogger<PluginHost> log)
{
    private readonly IPlugin[] _plugins = plugins.ToArray();

    public async Task DispatchAsync(EntryEvent e, CancellationToken ct = default)
    {
        foreach (var p in _plugins)
        {
            try { await p.OnEventAsync(e, ct); }
            catch (Exception ex) { log.LogError(ex, "plugin {Plugin} failed", p.Name); }
        }
    }
}
