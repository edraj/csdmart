// dmart CLI — interactive client for dmart REST API.
// Port of Python cli/cli.py.
//
// Usage:
//   dmart-cli                          # REPL mode (interactive shell)
//   dmart-cli c <space> <command...>   # Single command mode
//   dmart-cli s <script_file>          # Script mode (batch)

using Dmart.Cli;
using Spectre.Console;

var settings = CliSettings.Load();

AnsiConsole.MarkupLine("[bold green]DMART[/] [bold yellow]Command line interface[/]");
AnsiConsole.MarkupLine($"Connecting to [yellow]{settings.Url}[/] user: [yellow]{settings.Shortname}[/]");

using var dmart = new DmartClient(settings);
var handler = new CommandHandler(dmart, settings);

// Login
var (ok, error) = await dmart.LoginAsync();
if (!ok)
{
    AnsiConsole.MarkupLine($"[red]Login failed: {Markup.Escape(error ?? "unknown error")}[/]");
    return 1;
}

// Load spaces
await dmart.FetchSpacesAsync();
await dmart.ListAsync();
PrintSpaces();

// Determine mode
var mode = "repl";
var cmdArgs = args;

if (args.Length >= 1)
{
    switch (args[0].ToLowerInvariant())
    {
        case "c" or "cmd":
            mode = "cmd";
            // c <space> [/subpath] <command...>
            var offset = 1;
            if (args.Length >= 2)
            {
                SwitchSpace(args[1]);
                offset = 2;
                if (args.Length >= 3 && args[2].StartsWith('/'))
                {
                    dmart.CurrentSubpath = args[2];
                    offset = 3;
                }
            }
            cmdArgs = args[offset..];
            break;
        case "s" or "script":
            mode = "script";
            cmdArgs = args[1..];
            break;
    }
}

switch (mode)
{
    case "cmd":
        await handler.ExecuteAsync(string.Join(' ', cmdArgs));
        break;

    case "script":
        if (cmdArgs.Length < 1) { AnsiConsole.MarkupLine("[red]Usage: dmart-cli s <script_file>[/]"); return 1; }
        await RunScriptAsync(cmdArgs[0]);
        break;

    default: // repl
        AnsiConsole.MarkupLine("[red]Type [bold]?[/] for help[/]");
        await ReplAsync();
        break;
}

AnsiConsole.MarkupLine("[yellow]Good bye![/]");
return 0;

// ---- REPL ----

async Task ReplAsync()
{
    var historyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dmart", "cli_history");
    Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);

    // Set up ReadLine with autocomplete and history
    ReadLine.AutoCompletionHandler = new DmartCompleter(dmart);
    ReadLine.HistoryEnabled = true;
    if (File.Exists(historyPath))
    {
        foreach (var line in File.ReadAllLines(historyPath).TakeLast(500))
            ReadLine.AddHistory(line);
    }

    while (true)
    {
        try
        {
            var prompt = $"{dmart.CurrentSpace}:{dmart.CurrentSubpath} > ";
            var input = ReadLine.Read(prompt);
            if (input is null) break; // Ctrl+D

            input = input.Trim();
            if (string.IsNullOrEmpty(input)) continue;

            if (input is "exit" or "q" or "quit") break;

            ReadLine.AddHistory(input);
            await handler.ExecuteAsync(input);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
        }
    }

    // Save history
    try { await File.WriteAllLinesAsync(historyPath, ReadLine.GetHistory().TakeLast(1000).ToArray()); }
    catch { /* ignore */ }
}

// ---- Script mode ----

async Task RunScriptAsync(string scriptPath)
{
    if (!File.Exists(scriptPath))
    {
        AnsiConsole.MarkupLine($"[red]Script not found: {Markup.Escape(scriptPath)}[/]");
        return;
    }

    var variables = new Dictionary<string, string>();
    var inCommentBlock = false;

    foreach (var rawLine in await File.ReadAllLinesAsync(scriptPath))
    {
        var line = rawLine.Trim();
        if (line.StartsWith("/*")) { inCommentBlock = true; continue; }
        if (line.StartsWith("*/")) { inCommentBlock = false; continue; }
        if (inCommentBlock) continue;
        if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;

        // Variable assignment: VAR $name value
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0] == "VAR")
        {
            variables[parts[1]] = parts[2];
            continue;
        }

        // Variable substitution
        foreach (var (k, v) in variables)
            line = line.Replace(k, v);

        AnsiConsole.MarkupLine($"[green]> {Markup.Escape(line)}[/]");
        await handler.ExecuteAsync(line);
    }
}

// ---- Helpers ----

void SwitchSpace(string prefix)
{
    foreach (var name in dmart.SpaceNames)
    {
        if (name.StartsWith(prefix))
        {
            dmart.CurrentSpace = name;
            dmart.CurrentSubpath = "/";
            return;
        }
    }
}

void PrintSpaces()
{
    var parts = dmart.SpaceNames.Select(s =>
        s == dmart.CurrentSpace ? $"[bold yellow]{s}[/]" : $"[blue]{s}[/]");
    AnsiConsole.MarkupLine($"Available spaces: {string.Join("  ", parts)}");
}
