namespace Dmart.Plugins;

public interface IPlugin
{
    string Name { get; }
    Task OnEventAsync(EntryEvent e, CancellationToken ct = default);
}
