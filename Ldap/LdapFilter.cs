using System.Formats.Asn1;
using System.Text;

namespace Dmart.Ldap;

// RFC 4511 §4.5.1.7 search filters: the AST, its BER decoding, and evaluation
// against an LdapEntry.
//
// Evaluation is three-valued, as the RFC requires: a comparison on an
// attribute the entry does not carry, or with a matching rule the face does not
// implement, is Undefined rather than False. The difference is visible —
// `(!(fooBar=1))` matches nothing, not everything — and a client that relies on
// it would otherwise get entries it never asked for.
internal abstract record LdapFilter
{
    // Nested and/or/not past this depth is refused. Real filters nest two or
    // three deep; the limit exists because the decoder recurses, and a crafted
    // filter is otherwise a stack-depth attack on an unauthenticated socket.
    public const int MaxDepth = 32;

    public static LdapFilter Decode(AsnReader reader, int messageId, int depth = 0)
    {
        if (depth > MaxDepth) throw new LdapProtocolException("filter nested too deeply", messageId);
        var tag = reader.PeekTag();
        if (tag.TagClass != TagClass.ContextSpecific)
            throw new LdapProtocolException("filter is not a context-specific CHOICE", messageId);

        switch (tag.TagValue)
        {
            case 0:
            case 1:
            {
                var set = reader.ReadSetOf(skipSortOrderValidation: true, new Asn1Tag(TagClass.ContextSpecific, tag.TagValue, isConstructed: true));
                var children = new List<LdapFilter>();
                while (set.HasData) children.Add(Decode(set, messageId, depth + 1));
                return tag.TagValue == 0 ? new AndFilter(children) : new OrFilter(children);
            }
            case 2:
            {
                // `not [2] Filter` — a CHOICE cannot be implicitly tagged, so
                // this is an explicit wrapper around exactly one filter.
                var inner = reader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 2, isConstructed: true));
                var child = Decode(inner, messageId, depth + 1);
                inner.ThrowIfNotEmpty();
                return new NotFilter(child);
            }
            case 3: return ReadAssertion(reader, 3, FilterKind.Equality, messageId);
            case 5: return ReadAssertion(reader, 5, FilterKind.GreaterOrEqual, messageId);
            case 6: return ReadAssertion(reader, 6, FilterKind.LessOrEqual, messageId);
            case 8: return ReadAssertion(reader, 8, FilterKind.Approx, messageId);
            case 4:
            {
                var seq = reader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true));
                var attr = ReadString(seq);
                var parts = seq.ReadSequence();
                string? initial = null, final = null;
                var any = new List<string>();
                while (parts.HasData)
                {
                    var p = parts.PeekTag();
                    var value = ReadString(parts, new Asn1Tag(TagClass.ContextSpecific, p.TagValue));
                    switch (p.TagValue)
                    {
                        case 0: initial = value; break;
                        case 1: any.Add(value); break;
                        case 2: final = value; break;
                        default: throw new LdapProtocolException("bad substring component", messageId);
                    }
                }
                return new SubstringFilter(attr, initial, any, final);
            }
            case 7:
                return new PresentFilter(ReadString(reader, new Asn1Tag(TagClass.ContextSpecific, 7)));
            case 9:
            {
                var seq = reader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 9, isConstructed: true));
                string? rule = null, attr = null, value = null;
                var dnAttributes = false;
                while (seq.HasData)
                {
                    var p = seq.PeekTag();
                    switch (p.TagValue)
                    {
                        case 1: rule = ReadString(seq, new Asn1Tag(TagClass.ContextSpecific, 1)); break;
                        case 2: attr = ReadString(seq, new Asn1Tag(TagClass.ContextSpecific, 2)); break;
                        case 3: value = ReadString(seq, new Asn1Tag(TagClass.ContextSpecific, 3)); break;
                        case 4: dnAttributes = seq.ReadBoolean(new Asn1Tag(TagClass.ContextSpecific, 4)); break;
                        default: throw new LdapProtocolException("bad extensible match component", messageId);
                    }
                }
                if (value is null) throw new LdapProtocolException("extensible match without a value", messageId);
                return new ExtensibleFilter(rule, attr, value, dnAttributes);
            }
            default:
                throw new LdapProtocolException($"unknown filter choice [{tag.TagValue}]", messageId);
        }
    }

    private static ComparisonFilter ReadAssertion(AsnReader reader, int tag, FilterKind kind, int messageId)
    {
        var seq = reader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, tag, isConstructed: true));
        var attr = ReadString(seq);
        var value = ReadString(seq);
        if (seq.HasData) throw new LdapProtocolException("trailing data in attribute value assertion", messageId);
        return new ComparisonFilter(kind, attr, value);
    }

    internal static string ReadString(AsnReader reader, Asn1Tag? tag = null)
        => Encoding.UTF8.GetString(reader.ReadOctetString(tag));

    // ----- evaluation -----

    public abstract Tri Evaluate(LdapEntry entry);

    // ----- anchors -----

    // The (attribute, value) equalities that every matching entry MUST satisfy
    // at least one of, or null when the filter has no such guarantee. This is
    // what lets a search for `(&(objectClass=x)(uid=alice))` become one indexed
    // lookup instead of a scan: an AND needs only one anchored child, an OR
    // needs every branch anchored (the union of their lookups covers it).
    public virtual IReadOnlyList<(string Attribute, string Value)>? Anchors(Func<string, bool> indexed) => null;

    // Whether every test of `attribute` in this filter is a plain, un-negated
    // equality; if so, the values they name are added to `values`. An entry
    // carrying only those of its values then matches exactly when the full
    // entry would, which lets the face skip loading a large multi-valued
    // attribute (a group's `member` list) that the filter only probes for
    // named values. Un-negated matters: with a value missing, a test reads
    // Undefined where the full entry reads False, which AND and OR treat
    // alike but NOT does not.
    public abstract bool TestsOnlyByEquality(string attribute, List<string> values);

    private protected static bool Names(string filterAttribute, string attribute)
        => LdapSchema.Canonical(filterAttribute).Equals(attribute, StringComparison.OrdinalIgnoreCase);
}

internal enum Tri { False, True, Undefined }

internal enum FilterKind { Equality, GreaterOrEqual, LessOrEqual, Approx }

internal sealed record AndFilter(IReadOnlyList<LdapFilter> Children) : LdapFilter
{
    public override Tri Evaluate(LdapEntry entry)
    {
        var result = Tri.True;
        foreach (var c in Children)
        {
            var r = c.Evaluate(entry);
            if (r == Tri.False) return Tri.False;
            if (r == Tri.Undefined) result = Tri.Undefined;
        }
        return result;
    }

    public override IReadOnlyList<(string Attribute, string Value)>? Anchors(Func<string, bool> indexed)
    {
        foreach (var c in Children)
            if (c.Anchors(indexed) is { } a) return a;
        return null;
    }

    public override bool TestsOnlyByEquality(string attribute, List<string> values)
        => Children.All(c => c.TestsOnlyByEquality(attribute, values));

    public override string ToString() => "(&" + string.Concat(Children) + ")";
}

internal sealed record OrFilter(IReadOnlyList<LdapFilter> Children) : LdapFilter
{
    public override Tri Evaluate(LdapEntry entry)
    {
        var result = Tri.False;
        foreach (var c in Children)
        {
            var r = c.Evaluate(entry);
            if (r == Tri.True) return Tri.True;
            if (r == Tri.Undefined) result = Tri.Undefined;
        }
        return result;
    }

    public override IReadOnlyList<(string Attribute, string Value)>? Anchors(Func<string, bool> indexed)
    {
        if (Children.Count == 0) return null;
        var all = new List<(string Attribute, string Value)>();
        foreach (var c in Children)
        {
            if (c.Anchors(indexed) is not { } a) return null;
            all.AddRange(a);
        }
        return all;
    }

    public override bool TestsOnlyByEquality(string attribute, List<string> values)
        => Children.All(c => c.TestsOnlyByEquality(attribute, values));

    public override string ToString() => "(|" + string.Concat(Children) + ")";
}

internal sealed record NotFilter(LdapFilter Child) : LdapFilter
{
    public override Tri Evaluate(LdapEntry entry) => Child.Evaluate(entry) switch
    {
        Tri.True => Tri.False,
        Tri.False => Tri.True,
        _ => Tri.Undefined,
    };

    public override bool TestsOnlyByEquality(string attribute, List<string> values)
    {
        var negated = new List<string>();
        return Child.TestsOnlyByEquality(attribute, negated) && negated.Count == 0;
    }

    public override string ToString() => $"(!{Child})";
}

internal sealed record ComparisonFilter(FilterKind Kind, string Attribute, string Value) : LdapFilter
{
    public override Tri Evaluate(LdapEntry entry)
    {
        var values = entry.Get(Attribute);
        if (values is null) return Tri.Undefined;
        var wanted = LdapSchema.NormalizeValue(Attribute, Value);
        foreach (var v in values)
        {
            var have = LdapSchema.NormalizeValue(Attribute, v);
            var hit = Kind switch
            {
                FilterKind.GreaterOrEqual => string.CompareOrdinal(have, wanted) >= 0,
                FilterKind.LessOrEqual => string.CompareOrdinal(have, wanted) <= 0,
                // approxMatch is implementation-defined; equality is a valid
                // (if strict) implementation of it.
                _ => string.Equals(have, wanted, StringComparison.Ordinal),
            };
            if (hit) return Tri.True;
        }
        return Tri.False;
    }

    public override IReadOnlyList<(string Attribute, string Value)>? Anchors(Func<string, bool> indexed)
        => Kind == FilterKind.Equality && indexed(LdapSchema.Canonical(Attribute))
            ? [(LdapSchema.Canonical(Attribute), Value)]
            : null;

    public override bool TestsOnlyByEquality(string attribute, List<string> values)
    {
        if (!Names(Attribute, attribute)) return true;
        // Approx is evaluated as equality (see Evaluate), so it qualifies;
        // the ordering forms need the whole value set.
        if (Kind is not (FilterKind.Equality or FilterKind.Approx)) return false;
        values.Add(Value);
        return true;
    }

    public override string ToString()
    {
        var op = Kind switch
        {
            FilterKind.GreaterOrEqual => ">=",
            FilterKind.LessOrEqual => "<=",
            FilterKind.Approx => "~=",
            _ => "=",
        };
        return $"({Attribute}{op}{Value})";
    }
}

internal sealed record SubstringFilter(string Attribute, string? Initial, IReadOnlyList<string> Any, string? Final) : LdapFilter
{
    public override Tri Evaluate(LdapEntry entry)
    {
        var values = entry.Get(Attribute);
        if (values is null) return Tri.Undefined;
        foreach (var v in values)
            if (Matches(LdapSchema.NormalizeValue(Attribute, v))) return Tri.True;
        return Tri.False;
    }

    private bool Matches(string value)
    {
        var pos = 0;
        if (Initial is not null)
        {
            var i = LdapSchema.NormalizeValue(Attribute, Initial);
            if (!value.StartsWith(i, StringComparison.Ordinal)) return false;
            pos = i.Length;
        }
        foreach (var a in Any)
        {
            var n = LdapSchema.NormalizeValue(Attribute, a);
            var at = value.IndexOf(n, pos, StringComparison.Ordinal);
            if (at < 0) return false;
            pos = at + n.Length;
        }
        if (Final is not null)
        {
            var f = LdapSchema.NormalizeValue(Attribute, Final);
            return value.Length - pos >= f.Length && value.EndsWith(f, StringComparison.Ordinal);
        }
        return true;
    }

    public override bool TestsOnlyByEquality(string attribute, List<string> values) => !Names(Attribute, attribute);

    public override string ToString()
        => $"({Attribute}={Initial}*{string.Concat(Any.Select(a => a + "*"))}{Final})";
}

internal sealed record PresentFilter(string Attribute) : LdapFilter
{
    // `(objectClass=*)` is the conventional "every entry" filter, and every
    // entry the face builds carries objectClass, so the generic rule covers it.
    public override Tri Evaluate(LdapEntry entry)
        => entry.Get(Attribute) is { Count: > 0 } ? Tri.True : Tri.False;

    public override bool TestsOnlyByEquality(string attribute, List<string> values) => !Names(Attribute, attribute);

    public override string ToString() => $"({Attribute}=*)";
}

internal sealed record ExtensibleFilter(string? MatchingRule, string? Attribute, string Value, bool DnAttributes) : LdapFilter
{
    // Only the form with no matching rule and no dnAttributes is supported: it
    // means "equality on this attribute". Anything else names semantics the
    // face does not implement, and Undefined is the honest answer.
    public override Tri Evaluate(LdapEntry entry)
        => MatchingRule is null && Attribute is not null && !DnAttributes
            ? new ComparisonFilter(FilterKind.Equality, Attribute, Value).Evaluate(entry)
            : Tri.Undefined;

    // dnAttributes matches values in the entry's DN, never `member`'s; only a
    // test that names the attribute itself counts.
    public override bool TestsOnlyByEquality(string attribute, List<string> values)
        => Attribute is null || !Names(Attribute, attribute);

    public override string ToString() => $"({Attribute}:{MatchingRule}:={Value})";
}
