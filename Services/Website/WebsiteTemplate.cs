using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dmart.Middleware;
using Microsoft.Extensions.FileProviders;

namespace Dmart.Services.Website;

// The presentation half of the website: layout.html with {{placeholders}},
// the stylesheet and script, and the icons and figures the landing page can
// name. The content half lives in the website space.
//
// Embedded in the binary (WebsiteTemplate/**), so a relocated single-file
// dmart can still build a site; `--template <dir>` swaps in an operator's own.
public sealed class WebsiteTemplate
{
    // Copied verbatim into the build's _site/ folder. Everything else in the
    // template is inlined into pages rather than served.
    private static readonly string[] AssetNames = ["site.css", "site.js", "favicon.svg", "og-card.png"];

    public string Layout { get; }
    public IReadOnlyDictionary<string, byte[]> Assets { get; }
    // Short content hash of the assets, appended as ?v= so a rebuilt site
    // never pairs new pages with a stale cached stylesheet.
    public string Version { get; }

    private readonly Dictionary<string, string> _icons;
    private readonly Dictionary<string, string> _figures;

    private WebsiteTemplate(
        string layout, Dictionary<string, byte[]> assets,
        Dictionary<string, string> icons, Dictionary<string, string> figures)
    {
        Layout = layout;
        Assets = assets;
        _icons = icons;
        _figures = figures;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, bytes) in assets.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(name));
            sha.AppendData(bytes);
        }
        Version = Convert.ToHexStringLower(sha.GetHashAndReset())[..10];
    }

    public string? Icon(string? name) => Lookup(_icons, name);
    public string? Figure(string? name) => Lookup(_figures, name);

    // Names come from content, so they are matched against what the template
    // actually ships rather than turned into a path.
    private static string? Lookup(Dictionary<string, string> set, string? name) =>
        name is not null && set.TryGetValue(name, out var svg) ? svg : null;

    public static WebsiteTemplate? LoadEmbedded()
    {
        var provider = ManifestXmlFileProvider.TryCreate(Assembly.GetExecutingAssembly(), "WebsiteTemplate");
        return provider is null ? null : Load(provider);
    }

    public static WebsiteTemplate? LoadDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        using var provider = new PhysicalFileProvider(Path.GetFullPath(dir));
        return Load(provider);
    }

    private static WebsiteTemplate? Load(IFileProvider provider)
    {
        var layout = provider.GetFileInfo("layout.html");
        if (!layout.Exists) return null;

        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in AssetNames)
        {
            var f = provider.GetFileInfo(name);
            if (f.Exists) assets[name] = ReadBytes(f);
        }
        return new WebsiteTemplate(
            Encoding.UTF8.GetString(ReadBytes(layout)),
            assets,
            ReadSvgs(provider, "icons"),
            ReadSvgs(provider, "figures"));
    }

    private static Dictionary<string, string> ReadSvgs(IFileProvider provider, string dir)
    {
        var set = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in provider.GetDirectoryContents(dir))
        {
            if (f.IsDirectory || !f.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
            set[f.Name[..^4]] = Encoding.UTF8.GetString(ReadBytes(f)).Trim();
        }
        return set;
    }

    private static byte[] ReadBytes(IFileInfo f)
    {
        using var s = f.CreateReadStream();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
