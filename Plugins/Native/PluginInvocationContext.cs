namespace Dmart.Plugins.Native;

// Carries the calling actor across the managed → native → managed boundary
// when a plugin's hook callback runs. NativeHookPlugin sets this before
// invoking the plugin and restores the previous value in finally; callbacks
// (e.g. QueryCb) read it to decide whose ACLs to apply.
//
// AsyncLocal flows on ExecutionContext, so the value set on the dispatcher
// thread is visible to the synchronous managed → native → managed callback
// re-entry without any extra plumbing.
public static class PluginInvocationContext
{
    private static readonly AsyncLocal<string?> _actor = new();
    public static string? CurrentActor
    {
        get => _actor.Value;
        set => _actor.Value = value;
    }
}
