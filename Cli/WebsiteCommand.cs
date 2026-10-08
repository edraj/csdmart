using Dmart.Services.Website;

namespace Dmart.Cli;

// `dmart website build` — generate the static site from the website space.
//
// A command rather than something the server does on its own: content edited
// in dmart reaches the site when an operator rebuilds, which keeps a draft
// edit from going live by accident and keeps rendering out of the request
// path. The server picks the new build up on its next request; no restart.
internal static class WebsiteCommand
{
    private const string Usage =
        "Usage: dmart website build [--space <name>] [--base <url>] [--mount <path>]\n"
        + "                           [--out <dir>] [--template <dir>]";

    public static async Task<int> RunAsync(
        string[] args, string? dotenvPath, IDictionary<string, string?> dotenvValues)
    {
        var verb = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (verb != "build")
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        string? Opt(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        foreach (var flag in (string[])["--space", "--base", "--mount", "--out", "--template"])
        {
            if (Array.IndexOf(args, flag) is var i && i >= 0 && (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)))
            {
                Console.Error.WriteLine($"{flag} needs a value\n{Usage}");
                return 1;
            }
        }

        WebsiteTemplate? template;
        if (Opt("--template") is { } templateDir)
        {
            template = WebsiteTemplate.LoadDirectory(templateDir);
            if (template is null)
            {
                Console.Error.WriteLine($"no layout.html in template directory '{templateDir}'");
                return 1;
            }
        }
        else
        {
            template = WebsiteTemplate.LoadEmbedded();
            if (template is null)
            {
                Console.Error.WriteLine("this binary was built without the website template; pass --template <dir>");
                return 1;
            }
        }

        var (settings, db) = CliBootstrap.BuildFactoryOrExit(dotenvPath, dotenvValues);
        var options = new WebsiteBuildOptions(
            Space: Opt("--space") ?? "website",
            OutputRoot: Opt("--out") is { } outDir ? Path.GetFullPath(outDir) : WebsiteBuilder.ResolveRoot(settings),
            // The URL the server mounts the site under, unless the site will be
            // reached some other way (a proxy mapping a host's root onto it,
            // say), in which case links have to be built for that path instead.
            Mount: Opt("--mount") ?? settings.WebsiteUrl,
            BaseUrl: Opt("--base"),
            Template: template);

        WebsiteBuildResult result;
        try
        {
            result = await new WebsiteBuilder(CliBootstrap.BuildQueryService(settings, db)).BuildAsync(options);
        }
        catch (WebsiteBuildException ex)
        {
            Console.Error.WriteLine($"website: {ex.Message}");
            return 2;
        }

        foreach (var f in result.Files) Console.WriteLine($"  {f}");
        foreach (var w in result.Warnings) Console.Error.WriteLine($"warning: {w}");
        Console.WriteLine(
            $"website: {result.Pages} page(s){(result.Home ? " + home" : "")} → {result.BuildDir}");
        var served = WebsiteRenderer.NormalizeMount(settings.WebsiteUrl);
        var linked = WebsiteRenderer.NormalizeMount(options.Mount);
        Console.WriteLine($"         live at {served}/ on any dmart server whose WEBSITE_DIR is {options.OutputRoot}");
        if (linked != served)
            Console.WriteLine($"         links are built for {linked}/ — map that path onto {served}/ in front of dmart");
        return 0;
    }
}
