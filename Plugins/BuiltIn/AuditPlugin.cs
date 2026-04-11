namespace Dmart.Plugins.BuiltIn;

public sealed class AuditPlugin(ILogger<AuditPlugin> log) : IPlugin
{
    public string Name => "audit";
    public Task OnEventAsync(EntryEvent e, CancellationToken ct = default)
    {
        log.LogInformation("event {Kind} {Space}/{Subpath}/{Shortname}",
            e.Kind, e.Locator.SpaceName, e.Locator.Subpath, e.Locator.Shortname);
        return Task.CompletedTask;
    }
}
