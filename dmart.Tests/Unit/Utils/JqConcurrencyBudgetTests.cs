using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dmart.Utils;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Utils;

// The cap on how many `jq` subprocesses can be alive at once.
//
// Each run forks and buffers stdout in memory up to MaxOutputBytes (32MB).
// Unbounded, N concurrent requests meant N processes and up to N x 32MB of
// heap — and the jq path is reachable from an unauthenticated
// POST /public/query (an empty result set is still Status.Success with a
// non-null records[]), on a server that targets 512MB boards.
//
// Collection-disabled: Configure() replaces process-wide static state, so
// these must not run beside another test that shells out to jq.
[Collection("jq-budget")]
public class JqConcurrencyBudgetTests
{
    private static byte[] Input => Encoding.UTF8.GetBytes("[1]");

    [Fact]
    public async Task Work_Past_The_Budget_Is_Refused_Rather_Than_Queued_Forever()
    {
        // One slot, one second of patience. Six filters that each sleep well
        // past that: the first takes the slot, the rest must come back Busy
        // instead of piling up processes.
        JqRunner.Configure(maxConcurrency: 1, queueTimeoutSeconds: 1);
        try
        {
            // `until` spins inside jq; the run's own timeout ends it.
            const string Slow = "map(reduce range(0; 40000000) as $i (0; . + $i))";

            var runs = Enumerable.Range(0, 6)
                .Select(_ => JqRunner.RunAsync(Slow, Input, timeoutSeconds: 8))
                .ToArray();
            var results = await Task.WhenAll(runs);

            results.Count(r => r.Failure == JqRunner.FailureKind.Busy)
                .ShouldBeGreaterThan(0, "a saturated budget must shed load, not queue it");
        }
        finally
        {
            JqRunner.Configure(maxConcurrency: 4, queueTimeoutSeconds: 5);
        }
    }

    [Fact]
    public async Task Busy_Maps_To_A_Retryable_Failure_Not_A_Filter_Error()
    {
        // The distinction matters to clients: a Busy is the server's problem
        // and worth retrying, while JQ_ERROR means "your filter is wrong" and
        // retrying it identically is pointless.
        var resp = JqRunner.ToFailureResponse(JqRunner.FailureKind.Busy, null);

        resp.Error.ShouldNotBeNull();
        resp.Error!.Type.ShouldBe(Dmart.Models.Api.ErrorTypes.Internal);
        resp.Error!.Message.ShouldContain("capacity");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_Slot_Is_Returned_After_Every_Run()
    {
        // A leaked permit is a slow strangle rather than an outage: the budget
        // shrinks by one per failure until nothing runs. Exercises the success,
        // validation-reject and jq-error paths, then proves the budget is intact.
        JqRunner.Configure(maxConcurrency: 1, queueTimeoutSeconds: 5);
        try
        {
            (await JqRunner.RunAsync("map(.)", Input, 5)).Failure
                .ShouldBe(JqRunner.FailureKind.None);
            (await JqRunner.RunAsync("env", Input, 5)).Failure
                .ShouldBe(JqRunner.FailureKind.Invalid);
            (await JqRunner.RunAsync("map(.[syntax error", Input, 5)).Failure
                .ShouldBe(JqRunner.FailureKind.JqError);

            // The single slot still works -> nothing above kept it.
            (await JqRunner.RunAsync("map(.)", Input, 5)).Failure
                .ShouldBe(JqRunner.FailureKind.None);
        }
        finally
        {
            JqRunner.Configure(maxConcurrency: 4, queueTimeoutSeconds: 5);
        }
    }
}
