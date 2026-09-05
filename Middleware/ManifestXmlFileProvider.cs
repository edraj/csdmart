using System.Reflection;
using System.Xml.Linq;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Dmart.Middleware;

// An IFileProvider over embedded resources that reads
// Microsoft.Extensions.FileProviders.Embedded.Manifest.xml directly, instead of
// going through ManifestEmbeddedFileProvider.
//
// WHY THIS EXISTS. ManifestEmbeddedFileProvider does not work in the fully
// static musl build: constructing it throws, the SPA middlewares caught that
// and fell through to a filesystem fallback, and the static tarball ships
// exactly ONE file by design — so there was nothing on disk to fall back to
// and /cxb and /cat returned 404 on the one artifact whose entire selling
// point is being self-contained. The bytes were embedded the whole time; only
// the reader failed. Shipped in v1.5.0 and v1.5.1.
//
// Reading the manifest XML by hand is the same approach Cli/SeedCommand.cs
// already uses, for a different reason (that helper silently drops entries
// sharing a basename with a sibling directory). It works under Native AOT on
// both glibc and musl, which is why `languages loaded: … from embedded`
// appears in the static binary's own startup log while the SPAs did not: the
// language loader reads resources directly, the SPA middlewares did not.
//
// Path-traversal defence mirrors SeedCommand: manifest names are single
// segments, so anything containing "..", "/" or "\" is rejected rather than
// trusted. The manifest is generated at build time from the in-repo tree, so
// this is belt-and-braces against a compromised build artifact.
internal sealed class ManifestXmlFileProvider : IFileProvider
{
    private readonly Assembly _assembly;
    private readonly Dictionary<string, string> _files;      // "assets/app.js" -> resource path
    private readonly HashSet<string> _dirs;                  // "assets"
    private readonly DateTimeOffset _lastModified;

    private ManifestXmlFileProvider(
        Assembly assembly, Dictionary<string, string> files, HashSet<string> dirs, DateTimeOffset lastModified)
    {
        _assembly = assembly;
        _files = files;
        _dirs = dirs;
        _lastModified = lastModified;
    }

    // Returns null when the manifest is absent or carries nothing under
    // rootPath — i.e. a build that did not embed this SPA. Callers treat that
    // as "not built", exactly as they treated a ManifestEmbeddedFileProvider
    // whose index.html did not exist.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Trimming", "IL2026",
        Justification = "Resource names come from the embedded manifest XML at runtime and are passed straight to GetManifestResourceStream; no reflection over type metadata.")]
    public static ManifestXmlFileProvider? TryCreate(Assembly assembly, string rootPath)
    {
        try
        {
            using var manifest = assembly.GetManifestResourceStream(
                "Microsoft.Extensions.FileProviders.Embedded.Manifest.xml");
            if (manifest is null) return null;

            var xdoc = XDocument.Load(manifest);
            var ns = xdoc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var node = xdoc.Root?.Element(ns + "FileSystem");
            if (node is null) return null;

            // Walk down to rootPath ("cxb/dist/client") one segment at a time.
            foreach (var segment in rootPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                node = node.Elements(ns + "Directory")
                           .FirstOrDefault(d => (string?)d.Attribute("Name") == segment);
                if (node is null) return null;
            }

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Collect(node, ns, "", files, dirs);
            if (files.Count == 0) return null;

            // A constant would make every response's Last-Modified identical
            // across rebuilds; the binary's own timestamp is the closest thing
            // to "when these bytes were produced" that survives single-file AOT,
            // where Assembly.Location is empty.
            var stamp = DateTimeOffset.UnixEpoch;
            try
            {
                var self = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(self) && File.Exists(self))
                    stamp = File.GetLastWriteTimeUtc(self);
            }
            catch { /* keep the epoch */ }

            return new ManifestXmlFileProvider(assembly, files, dirs, stamp);
        }
        catch
        {
            return null;
        }
    }

    private static void Collect(
        XElement dir, XNamespace ns, string prefix,
        Dictionary<string, string> files, HashSet<string> dirs)
    {
        static bool Unsafe(string name) =>
            name == ".." || name.Contains('/') || name.Contains('\\');

        foreach (var f in dir.Elements(ns + "File"))
        {
            var name = (string?)f.Attribute("Name");
            var resource = (string?)f.Element(ns + "ResourcePath");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(resource)) continue;
            if (Unsafe(name)) continue;
            files[prefix.Length == 0 ? name : prefix + "/" + name] = resource;
        }

        foreach (var sub in dir.Elements(ns + "Directory"))
        {
            var name = (string?)sub.Attribute("Name");
            if (string.IsNullOrEmpty(name) || Unsafe(name)) continue;
            var next = prefix.Length == 0 ? name : prefix + "/" + name;
            dirs.Add(next);
            Collect(sub, ns, next, files, dirs);
        }
    }

    private static string Normalize(string subpath) =>
        subpath.Replace('\\', '/').TrimStart('/').TrimEnd('/');

    public IFileInfo GetFileInfo(string subpath)
    {
        var key = Normalize(subpath ?? string.Empty);
        if (key.Length != 0 && _files.TryGetValue(key, out var resource))
            return new ManifestFileInfo(_assembly, resource, key[(key.LastIndexOf('/') + 1)..], _lastModified);
        return new NotFoundFileInfo(subpath ?? string.Empty);
    }

    public IDirectoryContents GetDirectoryContents(string subpath)
    {
        var key = Normalize(subpath ?? string.Empty);
        if (key.Length != 0 && !_dirs.Contains(key)) return NotFoundDirectoryContents.Singleton;

        var prefix = key.Length == 0 ? "" : key + "/";
        var entries = new List<IFileInfo>();
        foreach (var (path, resource) in _files)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = path[prefix.Length..];
            if (rest.Contains('/')) continue;   // belongs to a subdirectory
            entries.Add(new ManifestFileInfo(_assembly, resource, rest, _lastModified));
        }
        return new EnumerableDirectoryContents(entries);
    }

    // Embedded content cannot change while the process runs.
    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private sealed class ManifestFileInfo(
        Assembly assembly, string resource, string name, DateTimeOffset lastModified) : IFileInfo
    {
        public bool Exists => true;
        public bool IsDirectory => false;
        public string? PhysicalPath => null;
        public string Name => name;
        public DateTimeOffset LastModified => lastModified;

        // StaticFileMiddleware needs this for Content-Length and range requests.
        // Manifest resource streams are seekable, so asking is cheap and exact.
        public long Length
        {
            get
            {
                using var s = Open();
                return s.Length;
            }
        }

        public Stream CreateReadStream() => Open();

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Trimming", "IL2026",
            Justification = "Resource name came from the embedded manifest XML; no reflection over type metadata.")]
        private Stream Open() =>
            assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"embedded resource missing: {resource}");
    }

    private sealed class EnumerableDirectoryContents(IReadOnlyList<IFileInfo> entries) : IDirectoryContents
    {
        public bool Exists => true;
        public IEnumerator<IFileInfo> GetEnumerator() => entries.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
