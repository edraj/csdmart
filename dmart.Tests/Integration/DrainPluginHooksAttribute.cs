using System.Reflection;
using Dmart.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace Dmart.Tests.Integration;

// Settle fire-and-forget plugin after-hooks between tests.
//
// TestParallelization.cs already runs the assembly serially, because the suite
// shares one database and process-global plugin state. That serializes TESTS,
// not their side effects: a concurrent after-hook is dispatched with Task.Run
// and outlives the request that triggered it, so work from one test can still
// be writing while the next one runs. The serialization guarantee quietly stops
// at the hook boundary.
//
// That is not theoretical. With the shipped plugins set to
// `"concurrent": true`, a full run reproducibly produced an extra failure in
// the OTP family — 2 occurrences in 6 runs, against 0 in 5 runs with them
// pinned to false. The tests were not wrong; the harness was letting one test's
// hooks run during another.
//
// The eight shipped after-hooks are pinned `"concurrent": false` today (see
// #234), so nothing is currently dispatched this way and this hook is a no-op
// that costs one dictionary check per test. It exists so flipping a plugin to
// fire-and-forget is a decision about that plugin, not a decision to accept an
// intermittent suite.
//
// Applied at assembly level in TestParallelization.cs, so no test class has to
// remember to opt in — remembering is exactly what would not happen.
public sealed class DrainPluginHooksAttribute : BeforeAfterTestAttribute
{
    // Generous because it only ever elapses when something is genuinely stuck:
    // the wait short-circuits when nothing is in flight, which is the normal
    // case. A hook still running after this is a bug worth surfacing as a slow
    // test rather than hiding by moving on.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    public override void After(MethodInfo methodUnderTest)
    {
        foreach (var factory in DmartFactory.Live)
        {
            PluginManager? plugins;
            try
            {
                plugins = factory.Services.GetService<PluginManager>();
            }
            catch (ObjectDisposedException)
            {
                // The factory was disposed while we were walking the snapshot.
                // Its hooks went with it; nothing left to settle.
                continue;
            }
            if (plugins is null) continue;

            // Sync-over-async: BeforeAfterTestAttribute.After has no async
            // form in xUnit v2. Safe here — xUnit runs it on a pool thread with
            // no SynchronizationContext to deadlock against.
            var settled = plugins.WaitForIdleAsync(Budget).GetAwaiter().GetResult();
            if (!settled)
            {
                // Loud rather than silent: an unsettled hook means the next
                // test starts with work still running, which is the condition
                // this attribute exists to prevent. Better a visible warning
                // than a mystery failure two tests later.
                Console.Error.WriteLine(
                    $"PLUGIN_HOOKS_NOT_SETTLED: after {methodUnderTest.DeclaringType?.Name}."
                    + $"{methodUnderTest.Name}, hooks were still running after "
                    + $"{Budget.TotalSeconds:0}s");
            }
        }
    }
}
