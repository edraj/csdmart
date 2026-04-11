using Dmart.Models.Core;
using Dmart.Plugins;

namespace Dmart.Services;

public sealed class EventBus(PluginHost host)
{
    public Task PublishCreatedAsync(Locator l, Entry e, CancellationToken ct = default)
        => host.DispatchAsync(new EntryEvent(EntryEventKind.Created, l, e), ct);

    public Task PublishUpdatedAsync(Locator l, Entry e, CancellationToken ct = default)
        => host.DispatchAsync(new EntryEvent(EntryEventKind.Updated, l, e), ct);

    public Task PublishDeletedAsync(Locator l, CancellationToken ct = default)
        => host.DispatchAsync(new EntryEvent(EntryEventKind.Deleted, l, null), ct);
}
