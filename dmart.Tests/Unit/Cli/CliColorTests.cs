using System.Text.Json;
using Dmart.Cli;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Cli;

// Pins the CLI's ANSI policy and the guarantee that follows from it: JSON
// written to a redirected stdout parses.
//
// The bug this locks down: `dmart version` emitted SGR escapes
// unconditionally, so `dmart version | jq -r .version` printed an empty
// string. jq doesn't error on that input, it just yields nothing — a silent
// failure that unattended scripts (the Raspberry Pi soak harness among them)
// had to work around by piping through `sed 's/\x1b\[[0-9;]*m//g'` before
// every jq call.
public class CliColorTests
{
    // ---- the decision ----

    [Theory]
    // Auto: a terminal gets color, a pipe does not.
    [InlineData(ColorMode.Auto, false, null, true)]
    [InlineData(ColorMode.Auto, true, null, false)]
    // NO_COLOR beats TTY detection. Per no-color.org any NON-EMPTY value
    // disables color — "0" and "false" mean "the variable is set", not "off".
    [InlineData(ColorMode.Auto, false, "1", false)]
    [InlineData(ColorMode.Auto, false, "0", false)]
    [InlineData(ColorMode.Auto, false, "false", false)]
    [InlineData(ColorMode.Auto, false, "", true)]
    // An explicit command-line option beats NO_COLOR — the spec says
    // user-specified options take precedence. This is what makes
    // `dmart --color=always version | less -R` work for someone who has
    // NO_COLOR exported globally.
    [InlineData(ColorMode.Always, true, "1", true)]
    [InlineData(ColorMode.Always, true, null, true)]
    [InlineData(ColorMode.Never, false, null, false)]
    [InlineData(ColorMode.Never, false, "", false)]
    public void Decide_Resolves_Mode_Then_NoColor_Then_Tty(
        ColorMode mode, bool redirected, string? noColor, bool expected)
    {
        CliColor.Decide(mode, redirected, noColor).ShouldBe(expected);
    }

    // ---- flag parsing ----

    [Theory]
    [InlineData(new[] { "--no-color", "version" }, ColorMode.Never)]
    [InlineData(new[] { "--color=never", "version" }, ColorMode.Never)]
    [InlineData(new[] { "--color=always", "version" }, ColorMode.Always)]
    [InlineData(new[] { "--color=auto", "version" }, ColorMode.Auto)]
    [InlineData(new[] { "--color", "always", "version" }, ColorMode.Always)]
    [InlineData(new[] { "version", "--color", "never" }, ColorMode.Never)]
    // Bare `--color` with nothing parseable after it means "yes, color" —
    // and must not swallow the positional that follows.
    [InlineData(new[] { "--color", "version" }, ColorMode.Always)]
    public void ParseFlags_Reads_Mode_And_Keeps_The_Subcommand(string[] argv, ColorMode expected)
    {
        var (rest, mode) = CliColor.ParseFlags(argv);
        mode.ShouldBe(expected);
        rest.ShouldBe(new[] { "version" });
    }

    // "no color flag here" has to stay distinguishable from "--color=auto".
    // Program.cs strips these flags globally before dispatch, so a subcommand
    // parsing its own argv sees none of them — and if that read as Auto, the
    // subcommand would silently undo the --no-color the user typed.
    [Fact]
    public void ParseFlags_Reports_Null_When_No_Color_Flag_Was_Given()
    {
        CliColor.ParseFlags(new[] { "version" }).Mode.ShouldBeNull();
        CliColor.ParseFlags(new[] { "cli", "c", "management", "ls" }).Mode.ShouldBeNull();
        CliColor.ParseFlags(new[] { "--color=auto", "version" }).Mode.ShouldBe(ColorMode.Auto);
    }

    [Fact]
    public void ParseFlags_Leaves_Unrelated_Args_In_Order()
    {
        var (rest, mode) = CliColor.ParseFlags(
            new[] { "export", "--no-color", "--space", "management", "--out", "x.zip" });
        mode.ShouldBe(ColorMode.Never);
        rest.ShouldBe(new[] { "export", "--space", "management", "--out", "x.zip" });
    }

    [Fact]
    public void ParseFlags_Last_Flag_Wins()
    {
        CliColor.ParseFlags(new[] { "--color=always", "--no-color" }).Mode.ShouldBe(ColorMode.Never);
        CliColor.ParseFlags(new[] { "--no-color", "--color=always" }).Mode.ShouldBe(ColorMode.Always);
    }

    // ---- the guarantee ----

    // The headline assertion: what a redirected stdout receives is a JSON
    // document, not a colorized rendering of one.
    [Fact]
    public void Json_Written_To_A_Redirected_Stdout_Parses_Cleanly()
    {
        // Exactly the shape `dmart version` builds.
        const string source =
            """
            {"version":"v1.5.12-0-g9460f86","branch":"master",
             "version_date":"2026-09-19","runtime":".NET 10.0.12"}
            """;
        using var doc = JsonDocument.Parse(source);

        var color = CliColor.Decide(ColorMode.Auto, redirected: true, noColor: null);
        color.ShouldBeFalse();

        var sw = new StringWriter();
        CliConsole.WriteJson(sw, doc.RootElement, color);
        var emitted = sw.ToString();

        emitted.ShouldNotContain("\u001b");

        // Parses, and round-trips every value — a document that parses but
        // has lost a field would still satisfy a bare Parse() call.
        using var reparsed = JsonDocument.Parse(emitted);
        reparsed.RootElement.GetProperty("version").GetString().ShouldBe("v1.5.12-0-g9460f86");
        reparsed.RootElement.GetProperty("branch").GetString().ShouldBe("master");
        reparsed.RootElement.GetProperty("version_date").GetString().ShouldBe("2026-09-19");
        reparsed.RootElement.GetProperty("runtime").GetString().ShouldBe(".NET 10.0.12");
    }

    // Stripping the SGR escapes out of the colorized form must yield exactly
    // the plain form. That is what pins the two modes to one emitter: if
    // someone reintroduces a separate no-color serializer, this fails.
    [Fact]
    public void Colorized_Output_Is_The_Plain_Output_Plus_Escapes()
    {
        using var doc = JsonDocument.Parse(
            """
            {"status":"success","records":[{"n":1,"ok":true,"nil":null}],"empty":{},"none":[]}
            """);

        var plain = new StringWriter();
        CliConsole.WriteJson(plain, doc.RootElement, color: false);

        var colored = new StringWriter();
        CliConsole.WriteJson(colored, doc.RootElement, color: true);

        colored.ToString().ShouldContain("\u001b[");
        System.Text.RegularExpressions.Regex
            .Replace(colored.ToString(), "\u001b\\[[0-9;]*m", "")
            .ShouldBe(plain.ToString());
    }

    // Nested structures, empty containers, and every scalar kind have to
    // survive the round trip — the emitter is hand-rolled, so this is the
    // only thing standing between it and a subtly malformed document.
    [Fact]
    public void Nested_Documents_Round_Trip_In_Both_Modes()
    {
        const string source =
            """
            {"status":"success","attributes":{"tags":["a","b"],"meta":{"deep":[[1,2],[]]}},
             "count":42,"ratio":0.5,"big":12345678901234567890,"flag":false,"missing":null,
             "empty_obj":{},"empty_arr":[]}
            """;
        using var doc = JsonDocument.Parse(source);

        foreach (var color in new[] { false, true })
        {
            var sw = new StringWriter();
            CliConsole.WriteJson(sw, doc.RootElement, color);
            var text = System.Text.RegularExpressions.Regex
                .Replace(sw.ToString(), "\u001b\\[[0-9;]*m", "");

            using var reparsed = JsonDocument.Parse(text);
            var root = reparsed.RootElement;
            root.GetProperty("count").GetInt32().ShouldBe(42);
            root.GetProperty("ratio").GetDouble().ShouldBe(0.5);
            // Preserved via GetRawText — going through double would lose it.
            root.GetProperty("big").GetRawText().ShouldBe("12345678901234567890");
            root.GetProperty("flag").GetBoolean().ShouldBeFalse();
            root.GetProperty("missing").ValueKind.ShouldBe(JsonValueKind.Null);
            root.GetProperty("empty_obj").EnumerateObject().Count().ShouldBe(0);
            root.GetProperty("empty_arr").GetArrayLength().ShouldBe(0);
            root.GetProperty("attributes").GetProperty("tags")[1].GetString().ShouldBe("b");
            root.GetProperty("attributes").GetProperty("meta")
                .GetProperty("deep")[0][1].GetInt32().ShouldBe(2);
        }
    }

    // A value carrying a quote, a backslash, a newline or a control character
    // used to be interpolated between two bare quote marks, which produced a
    // document that wouldn't parse even after the escapes were stripped.
    // Entry displaynames and error messages routinely contain these.
    [Fact]
    public void Strings_Needing_Escapes_Still_Produce_Valid_Json()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("quote\"key", "he said \"hi\"");
            w.WriteString("back\\slash", "C:\\tmp\\x");
            w.WriteString("newline", "line1\nline2\ttabbed");
            w.WriteString("control", "bell\u0007end");
            w.WriteString("unicode", "مدارت — ✓");
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(ms.ToArray());

        var sw = new StringWriter();
        CliConsole.WriteJson(sw, doc.RootElement, color: false);

        using var reparsed = JsonDocument.Parse(sw.ToString());
        reparsed.RootElement.GetProperty("quote\"key").GetString().ShouldBe("he said \"hi\"");
        reparsed.RootElement.GetProperty("back\\slash").GetString().ShouldBe("C:\\tmp\\x");
        reparsed.RootElement.GetProperty("newline").GetString().ShouldBe("line1\nline2\ttabbed");
        reparsed.RootElement.GetProperty("control").GetString().ShouldBe("bell\u0007end");
        reparsed.RootElement.GetProperty("unicode").GetString().ShouldBe("مدارت — ✓");
    }

    // End-to-end: spawn the real binary with stdout attached to a pipe and
    // feed what comes back to a JSON parser. The in-process tests above pin
    // the emitter; this one pins the wiring — that `dmart version` actually
    // reaches CliColor before it prints, which is the half that was broken.
    [Fact]
    public void Version_Subcommand_Emits_Parseable_Json_When_Stdout_Is_A_Pipe()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "dmart.dll");
        File.Exists(dll).ShouldBeTrue($"expected the dmart binary next to the tests at {dll}");

        // Run from an empty directory: DotEnv.Load walks up from the working
        // directory, and a config.env belonging to the developer's checkout
        // would make this depend on their local settings.
        var cwd = Directory.CreateTempSubdirectory("dmart-cli-color-").FullName;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = cwd,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(dll);
            psi.ArgumentList.Add("version");
            // Leave NO_COLOR out of it — the pipe alone has to be enough.
            psi.Environment.Remove("NO_COLOR");

            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(milliseconds: 120_000).ShouldBeTrue("dmart version timed out");
            proc.ExitCode.ShouldBe(0, stderr);

            stdout.ShouldNotContain("\u001b", customMessage:
                "dmart version wrote ANSI escapes into a redirected stdout; " +
                "`dmart version | jq -r .version` would yield an empty string");

            using var doc = JsonDocument.Parse(stdout);
            doc.RootElement.GetProperty("version").GetString().ShouldNotBeNullOrEmpty();
            doc.RootElement.GetProperty("runtime").GetString().ShouldStartWith(".NET");
        }
        finally
        {
            try { Directory.Delete(cwd, recursive: true); } catch (IOException) { }
        }
    }
}
