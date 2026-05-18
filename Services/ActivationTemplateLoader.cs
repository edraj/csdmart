using System.Net;
using System.Text.RegularExpressions;
using Dmart.Models.Core;
using Scriban;
using Scriban.Runtime;

namespace Dmart.Services;

// Loads the activation-email body templates (HTML + plain text) and renders
// them with per-user data through Scriban. The email is sent as
// multipart/alternative carrying both parts; each format has its own
// template, and each resolves independently:
//
//   1. Embedded default at templates/ActivationEmailContent.{html,txt} —
//      always present in the AOT release binary because dmart.csproj does
//      <EmbeddedResource Include="templates/*.{html,txt}"
//        LinkBase="templates" />.
//   2. Operator override at ~/.dmart/ActivationEmailContent.{html,txt} —
//      when present the override fully replaces the embedded template for
//      that format only. Operators can override either one, both, or
//      neither (single-file replace, no per-fragment merge).
//
// Variables exposed inside the templates: name, msisdn, shortname, link.
// All four are bound through a hand-built ScriptObject (no reflection) so the
// render path stays AOT/trim safe. HTML templates should `| html.escape` any
// variable that lands inside markup — see the embedded HTML default. The
// text template does not need html.escape since it is rendered as plain
// text by the recipient's mail client.
//
// The activation subject is rendered through the same engine (see
// RenderSubject) but its source comes from LanguageLoader (per-locale, with
// English fallback). Subject parsing is per-call rather than cached because
// the subject source varies by user.Language.
public sealed class ActivationTemplateLoader(ILogger<ActivationTemplateLoader> log)
{
    private Template? _htmlTemplate;
    private string _htmlSource = "<unloaded>";

    private Template? _textTemplate;
    private string _textSource = "<unloaded>";

    public void Load()
    {
        (_htmlTemplate, _htmlSource) = LoadFormat("html");
        (_textTemplate, _textSource) = LoadFormat("txt");
        if (_htmlTemplate is null && _textTemplate is null)
        {
            log.LogWarning("activation email templates not loaded — invitation emails will be empty");
        }
    }

    // Resolves one format (html or txt) through override → embedded → null,
    // then parses the source into a Scriban Template. Parse errors leave the
    // slot null and are logged; the other format still loads independently.
    private (Template?, string) LoadFormat(string ext)
    {
        var (text, origin) = TryLoadOverride(ext) ?? LoadEmbedded(ext) ?? (string.Empty, "<missing>");
        if (text.Length == 0)
        {
            log.LogWarning("activation email {Ext} template not loaded — that part will be empty", ext);
            return (null, origin);
        }
        try
        {
            var parsed = Template.Parse(text);
            if (parsed.HasErrors)
            {
                log.LogError("activation email {Ext} template parse failed from {Source}: {Errors}",
                    ext, origin, string.Join("; ", parsed.Messages));
                // Parse error on the override: fall through to the embedded
                // default for this format so an operator typo doesn't take
                // down the invitation flow.
                if (origin.StartsWith('/'))
                {
                    var fallback = LoadEmbedded(ext);
                    if (fallback is { } embedded)
                    {
                        try
                        {
                            var parsedEmbedded = Template.Parse(embedded.text);
                            if (!parsedEmbedded.HasErrors)
                            {
                                log.LogInformation("activation email {Ext} fell back to embedded {Source} after override parse error",
                                    ext, embedded.origin);
                                return (parsedEmbedded, embedded.origin);
                            }
                        }
                        catch (Exception ex)
                        {
                            log.LogError(ex, "activation email {Ext} embedded fallback also failed", ext);
                        }
                    }
                }
                return (null, origin);
            }
            log.LogInformation("activation email {Ext} template loaded from {Source}", ext, origin);
            return (parsed, origin);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "activation email {Ext} template compile failed from {Source}", ext, origin);
            return (null, origin);
        }
    }

    // Operator override at ~/.dmart/ActivationEmailContent.<ext> — same root
    // convention as the language overlay in LanguageLoader.
    private (string text, string origin)? TryLoadOverride(string ext)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;
        var path = Path.Combine(home, ".dmart", $"ActivationEmailContent.{ext}");
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
    // "{RootNamespace}.templates.ActivationEmailContent.<ext>". We match on
    // the ".templates." marker so the loader is independent of the root
    // namespace (same pattern as LanguageLoader's ".languages." matching).
    private (string text, string origin)? LoadEmbedded(string ext)
    {
        try
        {
            var assembly = typeof(ActivationTemplateLoader).Assembly;
            var marker = $".templates.ActivationEmailContent.{ext}";
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
            log.LogWarning(ex, "activation email {Ext} embedded resource scan failed", ext);
        }
        return null;
    }

    // Renders the loaded HTML body template with per-user data. Returns an
    // empty string when no template is loaded (logged as a warning at
    // Load()) so SmtpSender can decide whether to send a multipart message
    // or fall back to the text-only branch.
    public string RenderHtmlBody(User user, string link)
    {
        if (_htmlTemplate is null) return string.Empty;
        return Render(_htmlTemplate, user, link, _htmlSource);
    }

    // Renders the loaded text body template when one is available;
    // otherwise auto-derives a plain-text alternative from the rendered
    // HTML body so the multipart message always has a usable text part.
    // Auto-derive is the deepest fallback and only kicks in when both the
    // override .txt and the embedded .txt are unavailable (the embedded
    // .txt ships in the binary, so this branch is exercised mainly when
    // the operator has stripped the binary or the embedded resource fails
    // to parse).
    public string RenderTextBody(User user, string link)
    {
        if (_textTemplate is not null)
        {
            return Render(_textTemplate, user, link, _textSource);
        }
        if (_htmlTemplate is null) return string.Empty;
        var html = Render(_htmlTemplate, user, link, _htmlSource);
        return HtmlToPlainText.Convert(html);
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

// Narrow HTML-to-plain-text converter used only as the deepest fallback for
// the text part of the multipart activation email — when no .txt template
// is loaded we derive a text alternative from the rendered HTML body.
//
// This is NOT a general-purpose HTML parser. It assumes the input is the
// embedded activation HTML template (or an operator's HTML override) — a
// small, well-formed document with paragraph/break/anchor markup. The
// rules are intentionally narrow:
//
//   1. Replace common block-closers (</p>, </div>, </li>, </h1>..</h6>) and
//      every form of <br> with a newline.
//   2. Strip every remaining tag with the simple regex <[^>]+> (does not
//      handle CDATA / comments / scripts — none of those appear in the
//      activation template).
//   3. HTML-decode entities so &amp; / &lt; / &quot; come through as
//      literal characters.
//   4. Trim trailing whitespace per line and collapse runs of 3+ newlines
//      down to 2 so the output reads as paragraphs.
internal static class HtmlToPlainText
{
    private static readonly Regex BlockBreaks = new(
        @"</(p|div|li|h[1-6])\s*>|<br\s*/?\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnyTag = new(
        "<[^>]+>",
        RegexOptions.Compiled);

    private static readonly Regex CollapseBlankLines = new(
        @"\n{3,}",
        RegexOptions.Compiled);

    private static readonly Regex TrailingWhitespacePerLine = new(
        @"[ \t]+(?=\n)",
        RegexOptions.Compiled);

    public static string Convert(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var withBreaks = BlockBreaks.Replace(html, "\n");
        var stripped = AnyTag.Replace(withBreaks, string.Empty);
        var decoded = WebUtility.HtmlDecode(stripped);
        var trimmedLines = TrailingWhitespacePerLine.Replace(decoded, string.Empty);
        var collapsed = CollapseBlankLines.Replace(trimmedLines, "\n\n");
        return collapsed.Trim();
    }
}
