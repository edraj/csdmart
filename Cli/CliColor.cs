using Spectre.Console;

namespace Dmart.Cli;

// What the user asked for on the command line. `Auto` means "decide from the
// environment" and is the default; the other two are explicit overrides.
public enum ColorMode
{
    Auto,
    Always,
    Never,
}

// Single source of truth for "may we emit ANSI?".
//
// This used to be decided in three unrelated places — CliRunner checked
// Console.IsOutputRedirected for the `dmart cli` REPL, CliConsole's JSON
// printer checked nothing at all, and the stderr error paths in Program.cs
// hard-coded escapes. So `dmart version | jq` produced SGR-laced JSON that
// jq rejected, and it failed silently: jq emitted an empty string rather
// than an error, which is the worst way for this to go wrong.
//
// Note stdout and stderr are decided separately. `dmart version > out.json`
// on a terminal should still color the error text a failure would print to
// stderr — the redirect applies to one stream, not to the session.
internal static class CliColor
{
    // Resolve the policy for one stream. Pure so it can be tested without
    // touching the process's real handles or environment.
    //
    // Precedence, per https://no-color.org: an explicit command-line option
    // wins over NO_COLOR ("command-line options ... should take precedence"),
    // NO_COLOR wins over TTY detection, and any *non-empty* value of
    // NO_COLOR disables color regardless of what that value says — "0" and
    // "false" mean "set", not "off".
    public static bool Decide(ColorMode mode, bool redirected, string? noColor)
    {
        if (mode == ColorMode.Never) return false;
        if (mode == ColorMode.Always) return true;
        if (!string.IsNullOrEmpty(noColor)) return false;
        return !redirected;
    }

    private static bool _stdout = Decide(ColorMode.Auto, Console.IsOutputRedirected, NoColorEnv());
    private static bool _stderr = Decide(ColorMode.Auto, Console.IsErrorRedirected, NoColorEnv());

    public static ColorMode Mode { get; private set; } = ColorMode.Auto;

    // True when ANSI may be written to the corresponding stream.
    public static bool Stdout => _stdout;
    public static bool Stderr => _stderr;

    private static string? NoColorEnv() => Environment.GetEnvironmentVariable("NO_COLOR");

    // Adopt a mode and recompute both streams. Call once, early, after
    // flag parsing.
    public static void Apply(ColorMode mode)
    {
        Mode = mode;
        var noColor = NoColorEnv();
        _stdout = Decide(mode, Console.IsOutputRedirected, noColor);
        _stderr = Decide(mode, Console.IsErrorRedirected, noColor);
    }

    // Push the resolved stdout decision into Spectre's renderer. Spectre
    // decides ANSI from its own profile, so a Spectre table or a MarkupLine
    // would still emit escapes under NO_COLOR unless it is told. Kept out of
    // Apply() because touching AnsiConsole.Profile initializes Spectre's
    // console, and `dmart serve` has no reason to pay for that.
    public static void SyncSpectre()
    {
        if (!Stdout)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
    }

    // Force color off for both streams regardless of mode. Used by `--json`,
    // where decorative output is suppressed wholesale.
    public static void Disable()
    {
        Mode = ColorMode.Never;
        _stdout = false;
        _stderr = false;
    }

    // Pull the color flags out of an argument vector, returning the rest and
    // the mode they asked for — or null when the vector carried no color flag
    // at all. Null and ColorMode.Auto are deliberately distinct: Program.cs
    // strips these flags globally before dispatching, so by the time a
    // subcommand parses its own arguments there are none left, and a
    // subcommand that treated "none" as "Auto" would silently undo the
    // --no-color the user typed. Callers downstream of the global pass apply
    // only a non-null mode.
    //
    // Recognized:
    //   --no-color            same as --color=never
    //   --color=always|never|auto   (also: yes/no/force/tty/if-tty)
    //   --color always|never|auto   (two-token form; only consumes the next
    //                                token when it is a recognized value, so
    //                                a bare trailing `--color` can't eat a
    //                                positional argument)
    public static (string[] Args, ColorMode? Mode) ParseFlags(string[] args)
    {
        ColorMode? mode = null;
        var keep = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--no-color")
            {
                mode = ColorMode.Never;
                continue;
            }
            if (a.StartsWith("--color=", StringComparison.Ordinal))
            {
                mode = ParseValue(a[8..]) ?? mode;
                continue;
            }
            if (a == "--color")
            {
                if (i + 1 < args.Length && ParseValue(args[i + 1]) is { } next)
                {
                    mode = next;
                    i++;
                }
                else
                {
                    // Bare `--color` with no value means "yes, color".
                    mode = ColorMode.Always;
                }
                continue;
            }
            keep.Add(a);
        }
        return (keep.ToArray(), mode);
    }

    private static ColorMode? ParseValue(string value) => value.ToLowerInvariant() switch
    {
        "always" or "yes" or "force" or "true" or "on" => ColorMode.Always,
        "never" or "no" or "none" or "false" or "off" => ColorMode.Never,
        "auto" or "tty" or "if-tty" => ColorMode.Auto,
        _ => null,
    };
}
