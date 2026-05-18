using Dmart.Models.Core;
using Scriban;
using Scriban.Runtime;

namespace Dmart.Services;

// Loads the activation-email body template and renders it with per-user data
// through Scriban. The body source can come from one of two places:
//
//   1. Embedded default at templates/ActivationEmailContent.txt — always
//      present in the AOT release binary because dmart.csproj does
//      <EmbeddedResource Include="templates/*.txt" /> with LinkBase="templates".
//   2. Operator override at ~/.dmart/ActivationEmailContent.txt — when present
//      it fully replaces the embedded template (single-file override; no
//      per-fragment merge as we do for languages, since this template is a
//      single document).
//
// Variables exposed inside the template: name, msisdn, shortname, link.
// All four are bound through a hand-built ScriptObject (no reflection) so the
// render path stays AOT/trim safe. Templates should `| html.escape` any
// variable that lands inside HTML — see the embedded default.
//
// Adding a new variable? Add it to the `data` dict in Render(...) below.
// Keep the binding hand-built — switching to a reflection-based ScriptObject
// (e.g. `ScriptObject.Import(obj)`) would defeat the AOT-safety claim and
// the TrimmerRootAssembly justification in dmart.csproj.
//
// The activation subject is rendered through the same engine (see
// RenderSubject) but its source comes from LanguageLoader (per-locale, with
// English fallback). Subject parsing is per-call rather than cached because
// the subject source varies by user.Language.
public sealed class ActivationTemplateLoader(ILogger<ActivationTemplateLoader> log)
{
    private Template? _bodyTemplate;
    private string _bodySource = "<unloaded>";

    public void Load()
    {
        var (text, origin) = TryLoadOverride() ?? LoadEmbedded() ?? (string.Empty, "<missing>");
        _bodySource = origin;
        if (text.Length == 0)
        {
            log.LogWarning("activation email template not loaded — invitation emails will be empty");
            _bodyTemplate = null;
            return;
        }
        try
        {
            var parsed = Template.Parse(text);
            if (parsed.HasErrors)
            {
                log.LogError("activation email template parse failed from {Source}: {Errors}",
                    _bodySource, string.Join("; ", parsed.Messages));
                _bodyTemplate = null;
                return;
            }
            _bodyTemplate = parsed;
            log.LogInformation("activation email template loaded from {Source}", _bodySource);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "activation email template compile failed from {Source}", _bodySource);
            _bodyTemplate = null;
        }
    }

    // Operator override at ~/.dmart/ActivationEmailContent.txt — same root
    // convention as the language overlay in LanguageLoader.
    private (string text, string origin)? TryLoadOverride()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;
        var path = Path.Combine(home, ".dmart", "ActivationEmailContent.txt");
        if (!File.Exists(path)) return null;
        try
        {
            return (File.ReadAllText(path), path);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "activation email override read failed at {Path} — falling back to embedded", path);
            return null;
        }
    }

    // Embedded resource name follows MSBuild's dotted convention:
    // "{RootNamespace}.templates.ActivationEmailContent.txt". We match on the
    // ".templates." marker so the loader is independent of the root namespace
    // (same pattern as LanguageLoader's ".languages." matching).
    private (string text, string origin)? LoadEmbedded()
    {
        try
        {
            var assembly = typeof(ActivationTemplateLoader).Assembly;
            const string marker = ".templates.ActivationEmailContent.txt";
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                return (reader.ReadToEnd(), $"embedded:{name}");
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "activation email embedded resource scan failed");
        }
        return null;
    }

    // Renders the loaded body template with per-user data. Returns an empty
    // string when no template is loaded (logged as a warning at Load()) so
    // SmtpSender.SendEmailAsync still gets a string and the call site can
    // decide whether to short-circuit.
    public string RenderBody(User user, string link)
    {
        if (_bodyTemplate is null) return string.Empty;
        return Render(_bodyTemplate, user, link, _bodySource);
    }

    // Renders an inline template (used for the subject — the subject source
    // varies per user.Language so caching would be more code than the parse
    // cost saves). Returns the raw source on parse error so a malformed
    // subject template at worst sends an unrendered string rather than an
    // empty Subject line.
    public string RenderSubject(string source, User user, string link)
    {
        if (string.IsNullOrEmpty(source)) return string.Empty;
        try
        {
            var parsed = Template.Parse(source);
            if (parsed.HasErrors)
            {
                log.LogError("activation subject parse failed: {Errors}",
                    string.Join("; ", parsed.Messages));
                return source;
            }
            return Render(parsed, user, link, "subject");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "activation subject render failed");
            return source;
        }
    }

    private string Render(Template template, User user, string link, string source)
    {
        try
        {
            // Hand-built ScriptObject — no reflection-based property binding.
            // Python parity / current InvitationService.ActivationEmailBody:
            // displayname.en wins, falling back to shortname so recipients
            // always see a name. Null msisdn/link are mapped to empty so the
            // template doesn't need to defend against null.
            var name = user.Displayname?.En ?? user.Shortname ?? string.Empty;
            var data = new ScriptObject
            {
                { "name", name },
                { "msisdn", user.Msisdn ?? string.Empty },
                { "shortname", user.Shortname ?? string.Empty },
                { "link", link ?? string.Empty },
            };
            var context = new TemplateContext { StrictVariables = false };
            context.PushGlobal(data);
            return template.Render(context);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "activation template render failed from {Source}", source);
            return string.Empty;
        }
    }
}
