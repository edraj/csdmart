namespace Dmart.Services;

// Detects data directories that live on RAM-backed filesystems.
//
// WHY THIS EXISTS
//
// dmart will happily serve from a directory that disappears at the next reboot,
// and nothing about a running server looks wrong while it does. The failure was
// observed on a Raspberry Pi Zero 2 W running the diskless Alpine image, where
// the root filesystem is a tmpfs rebuilt at every boot and the real storage is
// an ext4 partition mounted over /var/lib/dmart. When the boot-media scan left
// that partition mounted read-only elsewhere, the mount silently did not happen
// and /var/lib/dmart stayed the tmpfs underneath — same path, same permissions,
// writable, empty. Health was green. Every write would have been lost at the
// next power cut, with no error at any layer to say so.
//
// That deployment caught it only because its OpenRC unit happened to carry an
// explicit check. Nothing in dmart itself would have said a word, and the same
// shape is available to anyone running a container with a volume that failed to
// mount — arguably the most common way to hit this.
//
// WHY A WARNING AND NOT A REFUSAL
//
// Running on tmpfs is legitimate: test suites, CI, throwaway demo instances and
// ephemeral containers all do it deliberately. Refusing to start would break
// those for the sake of a case the operator may well have chosen. The cost of
// the warning is one log line; the cost of being wrong about a refusal is a
// server that will not boot. So: say it loudly, at Warning, and let the operator
// decide.
public static class StorageDurability
{
    // Filesystems whose contents do not survive a reboot. Deliberately short:
    // "overlay" is NOT here, because an overlay's upper layer is usually on disk
    // and flagging every container would make this warning noise, which is how a
    // useful warning stops being read.
    private static readonly string[] RamBacked = ["tmpfs", "ramfs"];

    // The paths that hold data dmart cannot regenerate, and so must be durable.
    //
    // SpacesFolder is included only when set (it is opt-in, and empty means no
    // files are written). The SQLite file is included only when SQLite is the
    // active driver — warning about a stale SqlitePath default on a PostgreSQL
    // deployment would be noise, and the default is a bare relative filename.
    internal static IEnumerable<(string What, string Path)> DataPaths(
        Dmart.Config.DmartSettings settings, Dmart.DataAdapters.Sql.DatabaseDriver driver)
    {
        if (!string.IsNullOrWhiteSpace(settings.SpacesFolder))
            yield return ("spaces folder", settings.SpacesFolder);

        if (driver == Dmart.DataAdapters.Sql.DatabaseDriver.Sqlite
            && !string.IsNullOrWhiteSpace(settings.SqlitePath))
            yield return ("sqlite database", settings.SqlitePath);
    }

    // The filesystem type backing `path` when it is RAM-backed, else null.
    //
    // Linux-only by construction: it reads /proc/mounts. On any other platform,
    // and whenever /proc/mounts cannot be read, this returns null — the check is
    // an advisory extra, so being unable to perform it must never be an error.
    public static string? RamBackedFilesystem(string path)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            // The path need not exist yet — dmart creates its directories on
            // demand, and "would this be durable" is worth answering before the
            // first write, not after. So resolve it lexically rather than probing.
            return RamBackedFilesystem(Path.GetFullPath(path), File.ReadLines("/proc/mounts"));
        }
        catch (Exception)
        {
            // Unreadable /proc (a locked-down container, a non-standard mount
            // namespace) means "cannot tell", which is not "not durable".
            return null;
        }
    }

    // Core matcher. Separated so it can be tested against a synthetic mount
    // table rather than whatever the build machine happens to be mounting, and
    // takes an ALREADY-ABSOLUTE posix path so the test cases do not depend on
    // the host's path semantics (Path.GetFullPath("/var") is "C:\\var" on
    // Windows, which would make these tests pass or fail by build agent).
    internal static string? RamBackedFilesystem(string absolutePath, IEnumerable<string> procMountsLines)
    {
        var target = Normalize(absolutePath);

        string? bestType = null;
        var bestLength = -1;

        foreach (var line in procMountsLines)
        {
            // device mountpoint fstype options dump pass
            var parts = line.Split(' ');
            if (parts.Length < 3) continue;

            var mountPoint = Normalize(Unescape(parts[1]));
            var fsType = parts[2];

            if (!IsPrefixOf(mountPoint, target)) continue;

            // Longest matching mount point wins: /var/lib/dmart must beat "/",
            // which is what makes "ext4 mounted over a tmpfs root" read as ext4
            // rather than as tmpfs.
            if (mountPoint.Length > bestLength)
            {
                bestLength = mountPoint.Length;
                bestType = fsType;
            }
        }

        return bestType is not null && RamBacked.Contains(bestType) ? bestType : null;
    }

    // Trailing slashes are not significant to a mount point, but they break
    // string comparison, so strip them everywhere except on the root itself.
    private static string Normalize(string p) =>
        p.Length > 1 ? p.TrimEnd('/') : p;

    // Component-wise prefix test. A plain StartsWith would have "/var" claim
    // "/variable-data", attributing a path to an unrelated filesystem.
    private static bool IsPrefixOf(string mountPoint, string target)
    {
        if (mountPoint == "/") return true;
        if (!target.StartsWith(mountPoint, StringComparison.Ordinal)) return false;
        return target.Length == mountPoint.Length || target[mountPoint.Length] == '/';
    }

    // /proc/mounts escapes characters that would break its space-separated
    // format, as octal: space is \040, tab \011, newline \012, backslash \134.
    // A mount point containing a space is unusual but entirely legal, and
    // getting it wrong here would silently mis-attribute the path.
    private static string Unescape(string s)
    {
        if (!s.Contains('\\', StringComparison.Ordinal)) return s;

        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length
                && s[i + 1] is >= '0' and <= '7'
                && s[i + 2] is >= '0' and <= '7'
                && s[i + 3] is >= '0' and <= '7')
            {
                sb.Append((char)((s[i + 1] - '0') * 64 + (s[i + 2] - '0') * 8 + (s[i + 3] - '0')));
                i += 3;
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }
}
