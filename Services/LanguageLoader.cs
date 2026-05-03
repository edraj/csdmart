using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Enums;
using Dmart.Models.Json;

namespace Dmart.Services;

// Port of dmart Python's backend/languages/loader.py.
//
// Scans `{BaseDir}/languages/*.json` (and `{Cwd}/languages/*.json` for
// `dotnet run` from the source tree) and exposes a flat key lookup keyed by
// the file stem — same shape as Python's `languages: dict[str, dict[str, str]]`.
// The shipped files (english.json / arabic.json / kurdish.json) are copied to
// the output directory by dmart.csproj so the runtime layout matches Python.
public sealed class LanguageLoader(ILogger<LanguageLoader> log)
{
    private Dictionary<string, Dictionary<string, string>> _languages = new(StringComparer.OrdinalIgnoreCase);

    public void Load()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "languages"),
            Path.Combine(Directory.GetCurrentDirectory(), "languages"),
        };

        var loaded = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var scanned = false;
        foreach (var root in candidates)
        {
            if (!Directory.Exists(root)) continue;
            scanned = true;
            foreach (var file in Directory.EnumerateFiles(root, "*.json"))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                if (loaded.ContainsKey(stem)) continue; // first occurrence wins
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    var dict = JsonSerializer.Deserialize(bytes, DmartJsonContext.Default.DictionaryStringString);
                    if (dict is not null) loaded[stem] = dict;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "language load failed: {File}", file);
                }
            }
            if (loaded.Count > 0) break;
        }

        _languages = loaded;
        if (!scanned)
            log.LogInformation("languages dir not found — translations unavailable, callers fall back");
        else
            log.LogInformation("languages loaded: {Count} ({Names})", _languages.Count, string.Join(", ", _languages.Keys));
    }

    // Python parity: `languages[user.language][key]`. Returns the localized
    // string, falling back to English when the requested language has no
    // entry for the key (or the language file isn't loaded). Returns null
    // when neither the requested language nor English has the key — caller
    // decides what to do with a missing translation.
    public string? Get(Language lang, string key)
    {
        var stem = JsonbHelpers.EnumMember(lang);
        if (_languages.TryGetValue(stem, out var dict) && dict.TryGetValue(key, out var val))
            return val;
        if (!stem.Equals("english", StringComparison.OrdinalIgnoreCase)
            && _languages.TryGetValue("english", out var en) && en.TryGetValue(key, out var enVal))
            return enVal;
        return null;
    }
}
