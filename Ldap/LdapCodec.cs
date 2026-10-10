using System.Formats.Asn1;
using System.Text;

namespace Dmart.Ldap;

// LDAPv3 messages on the wire (RFC 4511 §4.1-4.2, BER per X.690).
//
// Decoding goes through System.Formats.Asn1 in BER mode, not DER. libldap 2.6
// (inside ldapsearch, Postfix, Dovecot and SSSD) happens to send minimal,
// DER-shaped lengths — LdapCodecTests pins its actual bytes — but LDAP is
// specified as BER, which also allows the non-minimal long form (`84 00 00 00
// 37` for 55). A DER reader would reject any client that uses it.
internal static class LdapCodec
{
    // ----- framing -----

    // Reads exactly one LDAPMessage TLV. Null on a clean EOF between messages.
    // The length is checked against maxBytes BEFORE anything is allocated, so
    // a client cannot make the server reserve memory by announcing a huge one.
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var head = new byte[6];
        if (!await ReadExactlyOrEofAsync(stream, head.AsMemory(0, 2), ct)) return null;
        if (head[0] != 0x30) throw new LdapProtocolException("message is not a SEQUENCE", 0);

        int length, headerLength;
        var first = head[1];
        if (first < 0x80)
        {
            length = first;
            headerLength = 2;
        }
        else
        {
            var n = first & 0x7F;
            // 0x80 is the indefinite form, which RFC 4511 §5.1 forbids; more
            // than four length octets cannot describe a message we would accept.
            if (n is 0 or > 4) throw new LdapProtocolException("unsupported length encoding", 0);
            await stream.ReadExactlyAsync(head.AsMemory(2, n), ct);
            long l = 0;
            for (var i = 0; i < n; i++) l = (l << 8) | head[2 + i];
            if (l > maxBytes) throw new LdapProtocolException($"message of {l} bytes exceeds the {maxBytes}-byte limit", 0);
            length = (int)l;
            headerLength = 2 + n;
        }

        var frame = new byte[headerLength + length];
        head.AsSpan(0, headerLength).CopyTo(frame);
        await stream.ReadExactlyAsync(frame.AsMemory(headerLength, length), ct);
        return frame;
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException("connection closed mid-message");
            }
            read += n;
        }
        return true;
    }

    // ----- decoding -----

    public static LdapRequest Decode(byte[] frame)
    {
        var messageId = 0;
        try
        {
            var outer = new AsnReader(frame, AsnEncodingRules.BER);
            var msg = outer.ReadSequence();
            if (!msg.TryReadInt32(out messageId) || messageId < 0)
                throw new LdapProtocolException("message id out of range", 0);

            var tag = msg.PeekTag();
            if (tag.TagClass != TagClass.Application)
                throw new LdapProtocolException("protocolOp is not an APPLICATION tag", messageId);

            // The op is decoded from a copy of its own TLV so the controls,
            // which follow it, can be read off `msg` independently.
            var opBytes = msg.ReadEncodedValue();
            var controls = msg.HasData ? ReadControls(msg, messageId) : [];

            var op = new AsnReader(opBytes, AsnEncodingRules.BER);
            return tag.TagValue switch
            {
                LdapOp.BindRequest => DecodeBind(op, messageId, controls),
                LdapOp.UnbindRequest => new LdapUnbindRequest(messageId, controls),
                LdapOp.SearchRequest => DecodeSearch(op, messageId, controls),
                LdapOp.AbandonRequest => DecodeAbandon(op, messageId, controls),
                LdapOp.ExtendedRequest => DecodeExtended(op, messageId, controls),
                LdapOp.ModifyRequest or LdapOp.AddRequest or LdapOp.DelRequest
                    or LdapOp.ModifyDnRequest or LdapOp.CompareRequest
                    => new LdapUnsupportedRequest(messageId, controls, tag.TagValue),
                _ => throw new LdapProtocolException($"unknown operation [APPLICATION {tag.TagValue}]", messageId),
            };
        }
        catch (AsnContentException ex)
        {
            throw new LdapProtocolException("malformed BER: " + ex.Message, messageId);
        }
    }

    private static LdapBindRequest DecodeBind(AsnReader op, int id, IReadOnlyList<LdapControl> controls)
    {
        var seq = op.ReadSequence(new Asn1Tag(TagClass.Application, LdapOp.BindRequest, isConstructed: true));
        if (!seq.TryReadInt32(out var version)) throw new LdapProtocolException("bad bind version", id);
        var name = LdapFilter.ReadString(seq);
        var auth = seq.PeekTag();
        if (auth.TagClass == TagClass.ContextSpecific && auth.TagValue == 0)
        {
            var password = LdapFilter.ReadString(seq, new Asn1Tag(TagClass.ContextSpecific, 0));
            return new LdapBindRequest(id, controls, version, name, password, null);
        }
        if (auth.TagClass == TagClass.ContextSpecific && auth.TagValue == 3)
        {
            var sasl = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 3, isConstructed: true));
            return new LdapBindRequest(id, controls, version, name, null, LdapFilter.ReadString(sasl));
        }
        throw new LdapProtocolException("unknown authentication choice", id);
    }

    private static LdapSearchRequest DecodeSearch(AsnReader op, int id, IReadOnlyList<LdapControl> controls)
    {
        var seq = op.ReadSequence(new Asn1Tag(TagClass.Application, LdapOp.SearchRequest, isConstructed: true));
        var baseDn = LdapFilter.ReadString(seq);
        var scope = ReadEnumerated(seq, id);
        if (scope is < 0 or > 2) throw new LdapProtocolException("bad search scope", id);
        _ = ReadEnumerated(seq, id);   // derefAliases: the face has no aliases
        if (!seq.TryReadInt32(out var sizeLimit) || sizeLimit < 0) throw new LdapProtocolException("bad size limit", id);
        if (!seq.TryReadInt32(out var timeLimit) || timeLimit < 0) throw new LdapProtocolException("bad time limit", id);
        var typesOnly = seq.ReadBoolean();
        var filter = LdapFilter.Decode(seq, id);
        var attrSeq = seq.ReadSequence();
        var attributes = new List<string>();
        while (attrSeq.HasData)
        {
            if (attributes.Count >= 256) throw new LdapProtocolException("too many attributes requested", id);
            attributes.Add(LdapFilter.ReadString(attrSeq));
        }
        return new LdapSearchRequest(id, controls, baseDn, (LdapScope)scope, sizeLimit, timeLimit, typesOnly, filter, attributes);
    }

    private static LdapAbandonRequest DecodeAbandon(AsnReader op, int id, IReadOnlyList<LdapControl> controls)
    {
        op.TryReadInt32(out var target, new Asn1Tag(TagClass.Application, LdapOp.AbandonRequest));
        return new LdapAbandonRequest(id, controls, target);
    }

    private static LdapExtendedRequest DecodeExtended(AsnReader op, int id, IReadOnlyList<LdapControl> controls)
    {
        var seq = op.ReadSequence(new Asn1Tag(TagClass.Application, LdapOp.ExtendedRequest, isConstructed: true));
        var name = LdapFilter.ReadString(seq, new Asn1Tag(TagClass.ContextSpecific, 0));
        byte[]? value = seq.HasData ? seq.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 1)) : null;
        return new LdapExtendedRequest(id, controls, name, value);
    }

    private static List<LdapControl> ReadControls(AsnReader msg, int id)
    {
        var list = new List<LdapControl>();
        var seq = msg.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        while (seq.HasData)
        {
            if (list.Count >= 32) throw new LdapProtocolException("too many controls", id);
            var c = seq.ReadSequence();
            var oid = LdapFilter.ReadString(c);
            var critical = false;
            byte[]? value = null;
            if (c.HasData && c.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean)) critical = c.ReadBoolean();
            if (c.HasData) value = c.ReadOctetString();
            list.Add(new LdapControl(oid, critical, value));
        }
        return list;
    }

    // ENUMERATED shares INTEGER's content encoding. Read as raw content bytes
    // rather than through ReadEnumeratedValue<T>, which goes through
    // Enum reflection the AOT build is better off without.
    private static int ReadEnumerated(AsnReader reader, int id)
    {
        var bytes = reader.ReadEnumeratedBytes().Span;
        if (bytes.Length is 0 or > 4) throw new LdapProtocolException("bad ENUMERATED", id);
        var v = (sbyte)bytes[0];
        var result = (int)v;
        for (var i = 1; i < bytes.Length; i++) result = (result << 8) | bytes[i];
        return result;
    }

    // ----- encoding -----

    // A response is LDAPMessage { messageID, protocolOp [APPLICATION opTag] {...}, controls? }.
    private static byte[] Message(int messageId, int opTag, Action<AsnWriter> body, IReadOnlyList<LdapControl>? controls = null)
    {
        var w = new AsnWriter(AsnEncodingRules.BER);
        using (w.PushSequence())
        {
            w.WriteInteger(messageId);
            using (w.PushSequence(new Asn1Tag(TagClass.Application, opTag, isConstructed: true)))
                body(w);
            if (controls is { Count: > 0 })
            {
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
                {
                    foreach (var c in controls)
                    {
                        using (w.PushSequence())
                        {
                            w.WriteOctetString(Encoding.UTF8.GetBytes(c.Oid));
                            if (c.Critical) w.WriteBoolean(true);
                            if (c.Value is not null) w.WriteOctetString(c.Value);
                        }
                    }
                }
            }
        }
        return w.Encode();
    }

    private static void WriteResult(AsnWriter w, int code, string matchedDn, string message)
    {
        WriteEnumerated(w, code);
        w.WriteOctetString(Encoding.UTF8.GetBytes(matchedDn));
        w.WriteOctetString(Encoding.UTF8.GetBytes(message));
    }

    private static void WriteEnumerated(AsnWriter w, int value)
    {
        // Minimal two's-complement content, written with the ENUMERATED tag
        // (universal 10) as a pre-encoded TLV for the reason ReadEnumerated gives.
        var content = new List<byte>();
        var v = value;
        do { content.Insert(0, (byte)(v & 0xFF)); v >>= 8; }
        while (v != 0 && v != -1);
        if (value >= 0 && (content[0] & 0x80) != 0) content.Insert(0, 0);
        var tlv = new byte[2 + content.Count];
        tlv[0] = 0x0A;
        tlv[1] = (byte)content.Count;
        content.CopyTo(tlv, 2);
        w.WriteEncodedValue(tlv);
    }

    // Bind, search-done, and the refusals of every write op share LDAPResult.
    public static byte[] Result(int messageId, int opTag, int code, string message = "", string matchedDn = "",
        IReadOnlyList<LdapControl>? controls = null)
        => Message(messageId, opTag, w => WriteResult(w, code, matchedDn, message), controls);

    public static byte[] Extended(int messageId, int code, string message, string? responseName = null, byte[]? responseValue = null)
        => Message(messageId, LdapOp.ExtendedResponse, w =>
        {
            WriteResult(w, code, "", message);
            if (responseName is not null)
                w.WriteOctetString(Encoding.UTF8.GetBytes(responseName), new Asn1Tag(TagClass.ContextSpecific, 10));
            if (responseValue is not null)
                w.WriteOctetString(responseValue, new Asn1Tag(TagClass.ContextSpecific, 11));
        });

    // Unsolicited notice (RFC 4511 §4.4.1): message id 0, sent before the
    // server drops a connection it can no longer make sense of.
    public static byte[] NoticeOfDisconnection(int code, string message)
        => Extended(0, code, message, LdapOid.NoticeOfDisconnection);

    public static byte[] SearchEntry(int messageId, LdapEntry entry, AttributeSelection selection, bool typesOnly)
        => Message(messageId, LdapOp.SearchResultEntry, w =>
        {
            w.WriteOctetString(Encoding.UTF8.GetBytes(entry.Dn));
            using (w.PushSequence())
            {
                foreach (var (name, values) in entry.Attributes)
                {
                    if (!selection.Includes(name)) continue;
                    using (w.PushSequence())
                    {
                        w.WriteOctetString(Encoding.UTF8.GetBytes(name));
                        using (w.PushSetOf())
                        {
                            if (!typesOnly)
                                foreach (var v in values) w.WriteOctetString(Encoding.UTF8.GetBytes(v));
                        }
                    }
                }
            }
        });

    // RFC 2696 realSearchControlValue ::= SEQUENCE { size INTEGER, cookie OCTET STRING }
    public static (int Size, byte[] Cookie) ReadPagedControl(byte[]? value, int messageId)
    {
        if (value is null) throw new LdapProtocolException("paged results control without a value", messageId);
        try
        {
            var seq = new AsnReader(value, AsnEncodingRules.BER).ReadSequence();
            if (!seq.TryReadInt32(out var size) || size < 0) throw new LdapProtocolException("bad page size", messageId);
            return (size, seq.ReadOctetString());
        }
        catch (AsnContentException ex)
        {
            throw new LdapProtocolException("malformed paged results control: " + ex.Message, messageId);
        }
    }

    public static LdapControl PagedControl(byte[] cookie)
    {
        var w = new AsnWriter(AsnEncodingRules.BER);
        using (w.PushSequence())
        {
            w.WriteInteger(0);   // size estimate: "unknown" is allowed and honest
            w.WriteOctetString(cookie);
        }
        return new LdapControl(LdapOid.PagedResults, false, w.Encode());
    }
}

// Which attributes a search returns (RFC 4511 §4.5.1.8): none listed or "*"
// means all user attributes, "+" means all operational ones, "1.1" alone means
// none, and anything else is a name.
internal sealed class AttributeSelection
{
    private readonly bool _allUser;
    private readonly bool _allOperational;
    private readonly HashSet<string> _named = new(StringComparer.OrdinalIgnoreCase);

    public AttributeSelection(IReadOnlyList<string> requested)
    {
        if (requested.Count == 0) { _allUser = true; return; }
        foreach (var r in requested)
        {
            if (r == "*") _allUser = true;
            else if (r == "+") _allOperational = true;
            else if (r != "1.1") _named.Add(LdapSchema.Canonical(r));
        }
    }

    public bool Includes(string attribute)
    {
        if (_named.Contains(attribute)) return true;
        return LdapSchema.IsOperational(attribute) ? _allOperational : _allUser;
    }
}
