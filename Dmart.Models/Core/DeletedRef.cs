namespace Dmart.Models.Core;

// A record removed by a delete, addressed by (space, subpath, shortname).
// ToPath() renders "$space/$subpath/$shortname" for the force-delete response,
// collapsing an empty/"/" subpath so there are never doubled slashes.
public readonly record struct DeletedRef(string Space, string Subpath, string Shortname)
{
    public string ToPath()
    {
        var sub = Subpath.Trim('/');
        return sub.Length == 0 ? $"{Space}/{Shortname}" : $"{Space}/{sub}/{Shortname}";
    }
}
