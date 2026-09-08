namespace Dmart.Plugins.Native;

// Ambient actor and plugin identity for the duration of a single plugin
// exchange.
//
// Set by SubprocessPluginHost.SendAndReceive from the values its caller
// supplied — the hook's user_shortname, or the API request's resolved user —
// and restored in the matching finally. Read by the host callbacks the plugin
// makes during that exchange: NativePluginCallbacks.EmitSaveEntry and
// EmitUpdateUser tag history rows with the plugin marker, EmitPluginLog routes
// to `plugin.<shortname>`, and GetCallbackLogger names its category.
//
// Note that `query` does NOT read the actor from here. PluginCallbackDispatcher
// receives it as an explicit argument and passes it to EmitQuery, because whose
// permissions a query runs under is security-relevant and should not depend on
// ambient state being correctly in scope. This holder is for the incidental
// attribution above, where a missing value degrades a log line rather than
// widening access.
//
// Storage discipline — [ThreadStatic] is load-bearing.
//   * A callback is dispatched synchronously on the thread sitting inside
//     SendAndReceive's read loop, so the value set when the exchange opened is
//     exactly what the callback reads. (The blocking stdout read runs on the
//     pool via Task.Run, but nothing reads this context there.)
//   * Switching to AsyncLocal would cost an allocation per dispatch for no
//     benefit — there is no `await` between set and read.
//   * A plain `static` would race across concurrent plugin invocations on
//     different threads — never use that here.
//
// Nesting: callers must save the previous value and restore it in a `finally`,
// in case a plugin's side effects re-enter a dispatcher on the same thread.
internal static class PluginInvocationContext
{
    [ThreadStatic]
    private static string? _currentActor;

    public static string? CurrentActor
    {
        get => _currentActor;
        set => _currentActor = value;
    }

    // Shortname of the plugin currently executing on this thread. Read by
    // NativePluginCallbacks.EmitPluginLog to prefix the log category as
    // `plugin.<shortname>[.<sub>]` — prevents a plugin from impersonating
    // unrelated categories.
    [ThreadStatic]
    private static string? _currentShortname;

    public static string? CurrentShortname
    {
        get => _currentShortname;
        set => _currentShortname = value;
    }
}
