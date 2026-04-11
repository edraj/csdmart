namespace Dmart.Utils;

public static class PathUtils
{
    public static string Normalize(string subpath) =>
        string.IsNullOrWhiteSpace(subpath) ? "/" : "/" + subpath.Trim('/');

    public static string Join(params string[] parts) =>
        "/" + string.Join('/', parts.Select(p => p.Trim('/')).Where(p => p.Length > 0));
}
