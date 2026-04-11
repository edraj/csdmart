namespace Dmart.Plugins.BuiltIn;

public sealed class WebhookPlugin : IPlugin
{
    public string Name => "webhook";
    public Task OnEventAsync(EntryEvent e, CancellationToken ct = default) => Task.CompletedTask;
}
