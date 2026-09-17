using System.Text;
using System.Threading.Tasks;
using Dmart.Utils;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Utils;

// jq's module system, which the builtin blocklist never covered.
//
//     import "config" as $c {search:"/some/dir"}; $c
//         -> [{"db_password":"hunter2"}]        (verified against jq 1.8.2)
//
// `search` takes an absolute directory, so the directive reads any
// <dir>/<name>.json the server process can open — in dmart that means entry
// payloads, with no ACL anywhere on the path. `include` is the same read for a
// `.jq` file.
//
// It was never reachable, because both production call sites wrap the caller's
// filter as `map(<filter>)` and jq only accepts a directive at the TOP of a
// program. That makes the wrapper — not the validator — the thing that was
// holding, which is the wrong place for the guarantee to live: ValidateFilter
// is public and its contract is "safe to run".
public class JqModuleDirectiveTests
{
    [Theory]
    [InlineData("import \"config\" as $c {search:\"/etc\"}; $c")]
    [InlineData("include \"mod\" {search:\"/etc\"}; leak")]
    [InlineData("import\"config\" as $c; $c")]          // jq accepts no space
    [InlineData("include\"mod\"; leak")]
    [InlineData("   \n\t import   \"x\" as $y; $y")]     // leading whitespace
    [InlineData("# lead comment\nimport \"config\" as $c; $c")]  // jq allows comments first
    [InlineData("#a\n#b\n include \"m\"; leak")]
    public void Module_Directives_Are_Rejected(string filter)
    {
        JqRunner.ValidateFilter(filter, out var reason).ShouldBeFalse(filter);
        reason.ShouldNotBeNull();
    }

    // The check is anchored at the start of the program because that is the
    // only position jq accepts a directive in. Matching the bare keyword
    // anywhere would reject honest filters that merely mention it — and a
    // uniqueness/search filter over user content very plausibly does.
    [Theory]
    [InlineData("{shortname, subpath}")]
    [InlineData(".import")]                       // a field named "import"
    [InlineData(".include_flag")]
    [InlineData("{a: .imports}")]
    [InlineData(".[\"import\"]")]                 // bracket access to such a field
    [InlineData(".description | test(\"include\")")]
    [InlineData("map(select(.tags | index(\"important\")))")]
    [InlineData("{note: \"please import this\"}")]
    public void Ordinary_Filters_Are_Not_Caught_By_The_Anchor(string filter)
        => JqRunner.ValidateFilter(filter, out _).ShouldBeTrue(filter);

    [Fact]
    public async Task A_Module_Directive_Does_Not_Reach_Jq()
    {
        // End to end: rejected as Invalid before a process is ever started, so
        // it cannot read a file even if jq on this box would happily do it.
        var r = await JqRunner.RunAsync(
            "import \"config\" as $c {search:\"/etc\"}; $c",
            Encoding.UTF8.GetBytes("[]"), timeoutSeconds: 2);

        r.Failure.ShouldBe(JqRunner.FailureKind.Invalid);
        r.Output.ShouldBeNull();
    }

    [Fact]
    public async Task A_Leading_Dash_Is_Still_Treated_As_A_Filter_Not_An_Option()
    {
        // argv carries `--` before the filter, so jq parses this as a (bad)
        // program rather than an option. Without the separator jq answers with
        // its usage text, and which options exist is the installed jq's
        // business, not ours.
        var r = await JqRunner.RunAsync(
            "-n", Encoding.UTF8.GetBytes("[]"), timeoutSeconds: 5);

        r.Failure.ShouldBe(JqRunner.FailureKind.JqError);
        (r.Stderr ?? "").ShouldNotContain("Usage:");
    }
}
