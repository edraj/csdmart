using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Json;

namespace Dmart.Plugins.Native;

// Adapts a native .so hook plugin behind IHookPlugin so PluginManager
// dispatches to it identically to built-in plugins.
internal sealed class NativeHookPlugin(NativePluginHandle handle, string shortname) : IHookPlugin
{
    public string Shortname => shortname;

    public Task HookAsync(Event e, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(e, DmartJsonContext.Default.Event);
        var result = handle.CallHook(json);

        if (!string.IsNullOrEmpty(result))
        {
            using var doc = JsonDocument.Parse(result);
            if (doc.RootElement.TryGetProperty("status", out var status)
                && status.GetString() == "error")
            {
                var message = doc.RootElement.TryGetProperty("message", out var msg)
                    ? msg.GetString() ?? "Native plugin error"
                    : "Native plugin error";
                throw new InvalidOperationException(message);
            }
        }

        return Task.CompletedTask;
    }
}
