using Dmart.Cli;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Cli;

// `upload csv <type> <subpath> <schema> <file> [--update] [--start-row N]`.
// A flag the parser misreads is silent in the worst way: a mistyped
// `--start-row` that fell through as a positional, or was ignored, would
// re-import a file from row 1 — creating every row twice-over as "entry
// exists" failures, or re-applying an update the operator meant to resume.
public class CliUploadCsvArgsTests
{
    [Fact]
    public void Plain_Upload_Creates_From_The_First_Row()
    {
        var args = CommandHandler.ParseUploadCsvArgs(["content", "ussd", "msisdn_whitelist", "xxx.csv"]);

        args.ShouldNotBeNull();
        args.Value.Positional.ShouldBe(new[] { "content", "ussd", "msisdn_whitelist", "xxx.csv" });
        args.Value.IsUpdate.ShouldBeFalse();
        args.Value.StartRow.ShouldBe(1);
    }

    [Fact]
    public void Flags_Are_Read_Wherever_They_Appear()
    {
        var args = CommandHandler.ParseUploadCsvArgs(
            ["--update", "content", "ussd", "--start-row", "18331", "msisdn_whitelist", "xxx.csv"]);

        args.ShouldNotBeNull();
        args.Value.Positional.ShouldBe(new[] { "content", "ussd", "msisdn_whitelist", "xxx.csv" });
        args.Value.IsUpdate.ShouldBeTrue();
        args.Value.StartRow.ShouldBe(18331);
    }

    [Theory]
    [InlineData("content", "ussd", "msisdn_whitelist")]                                  // no file
    [InlineData("content", "ussd", "msisdn_whitelist", "xxx.csv", "extra")]              // one too many
    [InlineData("content", "ussd", "msisdn_whitelist", "xxx.csv", "--start-row")]        // no value
    [InlineData("content", "ussd", "msisdn_whitelist", "xxx.csv", "--start-row", "0")]   // rows start at 1
    [InlineData("content", "ussd", "msisdn_whitelist", "xxx.csv", "--start-row", "abc")]
    [InlineData("content", "ussd", "msisdn_whitelist", "xxx.csv", "--updte")]            // typo, not ignored
    public void Anything_Else_Is_Rejected(params string[] input)
        => CommandHandler.ParseUploadCsvArgs(input).ShouldBeNull();
}
