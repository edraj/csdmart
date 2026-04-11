namespace Dmart.Plugins.BuiltIn;

public sealed class NotificationPlugin : IPlugin
{
    public string Name => "notification";
    public Task OnEventAsync(EntryEvent e, CancellationToken ct = default) => Task.CompletedTask;
}
