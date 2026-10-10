using System.Globalization;
using System.Text;

namespace Dmart.Ldap;

// Distinguished names (RFC 4514), only as far as the directory face needs them:
// parse what clients send, compare two DNs for equality, and build DNs from
// dmart shortnames safely.
//
// Comparison is on the NORMALIZED form: attribute types folded to their
// canonical lowercase name (so `CN=x` and `commonName=x` are the same RDN),
// values unescaped then case-folded, insignificant spaces dropped. Every DN
// this directory serves uses case-insensitive naming attributes (dc, ou, cn,
// uid), so folding the value is correct for all of them.
internal static class LdapDn
{
    // One RDN component. Multi-valued RDNs (`cn=a+sn=b`) parse into several of
    // these sharing an RDN index; this directory never names an entry that way,
    // but a client may still send one, and it must compare as "no such entry"
    // rather than throw.
    internal readonly record struct Ava(int Rdn, string Type, string Value);

    public static bool TryParse(string dn, out List<Ava> avas)
    {
        avas = new List<Ava>();
        if (string.IsNullOrWhiteSpace(dn)) return true;   // the root DSE

        var i = 0;
        var rdn = 0;
        while (true)
        {
            SkipSpaces(dn, ref i);
            var typeStart = i;
            while (i < dn.Length && dn[i] != '=') i++;
            if (i >= dn.Length) return false;
            var type = dn[typeStart..i].Trim();
            if (type.Length == 0) return false;
            i++; // '='

            if (!TryReadValue(dn, ref i, out var value)) return false;
            avas.Add(new Ava(rdn, type, value));

            if (i >= dn.Length) return true;
            var sep = dn[i++];
            if (sep is ',' or ';') rdn++;
            else if (sep != '+') return false;
        }
    }

    // `uid=Alice, OU=People,dc=imx,dc=sh` → `uid=alice,ou=people,dc=imx,dc=sh`.
    // Null for an unparseable DN, which callers treat as matching nothing.
    public static string? Normalize(string dn)
    {
        if (!TryParse(dn, out var avas)) return null;
        var sb = new StringBuilder();
        foreach (var group in avas.GroupBy(a => a.Rdn))
        {
            if (sb.Length > 0) sb.Append(',');
            var parts = group
                .Select(a => LdapSchema.Canonical(a.Type).ToLowerInvariant() + "=" + Escape(Fold(a.Value)))
                .OrderBy(s => s, StringComparer.Ordinal);
            sb.AppendJoin('+', parts);
        }
        return sb.ToString();
    }

    public static bool AreEqual(string a, string b)
    {
        var na = Normalize(a);
        return na is not null && string.Equals(na, Normalize(b), StringComparison.Ordinal);
    }

    // RFC 4514 §2.4 escaping, for building a DN from a dmart shortname or
    // group name. Shortnames are restricted enough that this rarely fires, but
    // the face must never let a value smuggle in an extra RDN.
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var needs = c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '='
                || (i == 0 && (c is ' ' or '#'))
                || (i == value.Length - 1 && c == ' ');
            if (c == '\0') { sb.Append("\\00"); continue; }
            if (needs) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // Value folding shared with attribute matching: insignificant spaces
    // collapsed, then case-folded.
    internal static string Fold(string value)
        => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static void SkipSpaces(string s, ref int i)
    {
        while (i < s.Length && s[i] == ' ') i++;
    }

    // Reads one attribute value up to an unescaped separator, undoing the
    // escapes: `\,` style pairs and `\XX` hex pairs (UTF-8 bytes). Trailing
    // unescaped spaces are insignificant and dropped.
    private static bool TryReadValue(string dn, ref int i, out string value)
    {
        SkipSpaces(dn, ref i);
        var bytes = new List<byte>();
        var lastSignificant = 0;
        while (i < dn.Length && dn[i] is not (',' or ';' or '+'))
        {
            var c = dn[i];
            if (c == '\\')
            {
                if (i + 1 >= dn.Length) { value = ""; return false; }
                var next = dn[i + 1];
                if (i + 2 < dn.Length && Uri.IsHexDigit(next) && Uri.IsHexDigit(dn[i + 2]))
                {
                    bytes.Add(byte.Parse(dn.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 3;
                }
                else
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(next.ToString()));
                    i += 2;
                }
                lastSignificant = bytes.Count;
                continue;
            }
            // A surrogate pair is one character; encoding its halves separately
            // would turn each into U+FFFD.
            var width = char.IsHighSurrogate(c) && i + 1 < dn.Length && char.IsLowSurrogate(dn[i + 1]) ? 2 : 1;
            bytes.AddRange(Encoding.UTF8.GetBytes(dn.Substring(i, width)));
            if (c != ' ') lastSignificant = bytes.Count;
            i += width;
        }
        value = Encoding.UTF8.GetString(bytes.GetRange(0, lastSignificant).ToArray());
        return true;
    }
}
