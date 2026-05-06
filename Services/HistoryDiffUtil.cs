using System.Text.Json;

namespace Dmart.Services;

// Flatten + structural-equality helpers shared by EntryService and UserService
// for building Python-parity history diffs ({field_path: {old, new}}).
internal static class HistoryDiffUtil
{
    public static void FlattenJson(JsonElement el, string prefix, Dictionary<string, object?> outDict)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                    FlattenJson(prop.Value, $"{prefix}.{prop.Name}", outDict);
                break;
            case JsonValueKind.Array:
                // Store the cloned JsonElement so ValuesEqual walks it
                // structurally — GetRawText() compared two semantically-equal
                // arrays as unequal whenever JSONB's canonical form differed
                // from the client's re-serialized form (whitespace, key order,
                // numeric repr), making unchanged arrays show up in every diff.
                outDict[prefix] = el.Clone();
                break;
            case JsonValueKind.String:
                outDict[prefix] = el.GetString();
                break;
            case JsonValueKind.Number:
                outDict[prefix] = el.TryGetInt64(out var i) ? (object)i : el.GetDouble();
                break;
            case JsonValueKind.True:  outDict[prefix] = true; break;
            case JsonValueKind.False: outDict[prefix] = false; break;
            case JsonValueKind.Null:  outDict[prefix] = null; break;
        }
    }

    public static bool ValuesEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a is JsonElement ja && b is JsonElement jb) return JsonElementEquals(ja, jb);
        if (a is System.Collections.IEnumerable ae && a is not string
            && b is System.Collections.IEnumerable be && b is not string)
        {
            var al = new List<object?>();
            foreach (var x in ae) al.Add(x);
            var bl = new List<object?>();
            foreach (var x in be) bl.Add(x);
            if (al.Count != bl.Count) return false;
            for (var i = 0; i < al.Count; i++)
                if (!ValuesEqual(al[i], bl[i])) return false;
            return true;
        }
        return Equals(a, b);
    }

    // Structural JSON equality — same semantics as Python's `==` on the
    // deserialized (dict / list / primitive) tree. Key order is ignored for
    // objects so JSONB's canonicalization doesn't surface as a diff.
    public static bool JsonElementEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            // Allow int↔double when numerically equal — JSONB normalizes to a
            // single numeric representation; clients may send ints as either.
            if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number) { }
            else return false;
        }
        switch (a.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return true;
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return a.GetDouble() == b.GetDouble();
            case JsonValueKind.Array:
            {
                if (a.GetArrayLength() != b.GetArrayLength()) return false;
                using var aEnum = a.EnumerateArray().GetEnumerator();
                using var bEnum = b.EnumerateArray().GetEnumerator();
                while (aEnum.MoveNext() && bEnum.MoveNext())
                    if (!JsonElementEquals(aEnum.Current, bEnum.Current)) return false;
                return true;
            }
            case JsonValueKind.Object:
            {
                var aKeys = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var p in a.EnumerateObject()) aKeys[p.Name] = p.Value;
                var bKeys = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var p in b.EnumerateObject()) bKeys[p.Name] = p.Value;
                if (aKeys.Count != bKeys.Count) return false;
                foreach (var (k, av) in aKeys)
                {
                    if (!bKeys.TryGetValue(k, out var bv)) return false;
                    if (!JsonElementEquals(av, bv)) return false;
                }
                return true;
            }
            default:
                return false;
        }
    }
}
