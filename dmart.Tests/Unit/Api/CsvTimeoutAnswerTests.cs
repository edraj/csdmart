using Dmart.Api.Managed;
using Dmart.Middleware;
using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Api;

// The 504 a CSV import gets when it runs past REQUEST_TIMEOUT. Rows commit one
// at a time, so this answer is the only record of what a partial import did —
// and the only thing that tells the operator where to pick it up.
public sealed class CsvTimeoutAnswerTests
{
    private static RequestDeadline Deadline()
    {
        using var cts = new CancellationTokenSource();
        return new RequestDeadline(TimeSpan.FromSeconds(35), cts.Token, CancellationToken.None);
    }

    private static List<Dictionary<string, object>> Rows(int count)
        => Enumerable.Range(1, count)
            .Select(i => new Dictionary<string, object> { ["row"] = i, ["error"] = "schema mismatch" })
            .ToList();

    [Fact]
    public void Progress_Caps_The_Per_Row_Failure_List_But_Not_Its_Count()
    {
        // A 100,000-row CSV whose header does not match the schema fails every
        // row. Copying all of them into the error body meant serializing tens of
        // megabytes of JSON on a request that had ALREADY been declared over its
        // time budget — and the dialog then held the lot before trimming it for
        // display.
        var cut = new CsvImportInterruptedException(
            1, 100_000, 0, Rows(CsvService.MaxFailedInError + 500), finished: false, CancellationToken.None);

        var info = ResourceWithPayloadHandler.CsvTimeoutInfo(cut)!;

        ((List<Dictionary<string, object>>)info[0]["failed"]).Count.ShouldBe(CsvService.MaxFailedInError);
        info[0]["failed_count"].ShouldBe(CsvService.MaxFailedInError + 500);
        info[0]["failed_truncated"].ShouldBe(true);
    }

    [Fact]
    public void Progress_Is_Whole_When_It_Fits()
    {
        var cut = new CsvImportInterruptedException(
            1, 7, 5, Rows(2), finished: false, CancellationToken.None);

        var info = ResourceWithPayloadHandler.CsvTimeoutInfo(cut)!;

        ((List<Dictionary<string, object>>)info[0]["failed"]).Count.ShouldBe(2);
        info[0].ShouldNotContainKey("failed_truncated");
        info[0]["inserted"].ShouldBe(5);
        info[0]["resume_row"].ShouldBe(8);
    }

    [Fact]
    public void An_Import_That_Only_Ran_Late_Has_Nothing_To_Resume()
    {
        var cut = new CsvImportInterruptedException(
            1, 7, 7, [], finished: true, CancellationToken.None);

        ResourceWithPayloadHandler.CsvTimeoutInfo(cut)![0].ShouldNotContainKey("resume_row");
        ResourceWithPayloadHandler.CsvTimeoutMessage(cut, Deadline(), isUpdate: false)
            .ShouldContain("The import finished");
    }

    [Fact]
    public void An_Interruption_That_Measured_Nothing_Reports_Nothing()
    {
        // The standard exception constructors (CA1032) carry defaults, not
        // measurements: LastRow 0 and FirstRow 1 make ResumeRow 1. Quoting that
        // would tell an operator whose import had already written most of the
        // file to start again from the top, duplicating every auto-shortname
        // row — so an unmeasured interruption must say so instead.
        var cut = new CsvImportInterruptedException("wrapped and rethrown");

        cut.KnowsProgress.ShouldBeFalse();
        ResourceWithPayloadHandler.CsvTimeoutInfo(cut).ShouldBeNull();
        var message = ResourceWithPayloadHandler.CsvTimeoutMessage(cut, Deadline(), isUpdate: false);
        message.ShouldContain("not");
        message.ShouldNotContain("start_row=");
    }
}
