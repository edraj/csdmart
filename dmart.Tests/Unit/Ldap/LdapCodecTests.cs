using System.Formats.Asn1;
using System.Text;
using Dmart.Ldap;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Ldap;

// The LDAP face's wire codec, against bytes real clients send rather than bytes
// this codec would produce itself.
public sealed class LdapCodecTests
{
    // Captured from openldap-clients 2.6.14 (Fedora 43):
    //   ldapsearch -x -D cn=dex,ou=services,dc=imx,dc=sh -w Service12345 \
    //     -b ou=people,dc=imx,dc=sh \
    //     '(&(&(objectClass=freexUser)(isActive=TRUE)(authorizedService=matrix))(uid=alice))' uid mail
    // — the filter matrix-deploy's Dex connector sends, plus the uid clause Dex
    // appends. Recorded by a stub server that answered the bind with success.
    private const string LibldapBind =
        "30370201016032020103041f636e3d6465782c6f753d73657276696365732c64633d696d782c64633d7368800c536572766963653132333435";
    private const string LibldapSearch =
        "30819502010263818f04166f753d70656f706c652c64633d696d782c64633d73680a01020a0100020100020100010100a059a049a318040b6f626a656374436c6173730409667265657855736572a31004086973416374697665040454525545a31b0411617574686f72697a65645365727669636504066d6174726978a30c04037569640405616c696365300b040375696404046d61696c";
    private const string LibldapUnbind = "30050201034200";

    [Fact]
    public void Decodes_A_Libldap_Simple_Bind()
    {
        var bind = LdapCodec.Decode(Convert.FromHexString(LibldapBind)).ShouldBeOfType<LdapBindRequest>();
        bind.MessageId.ShouldBe(1);
        bind.Version.ShouldBe(3);
        bind.Name.ShouldBe("cn=dex,ou=services,dc=imx,dc=sh");
        bind.SimplePassword.ShouldBe("Service12345");
        bind.SaslMechanism.ShouldBeNull();
    }

    [Fact]
    public void Decodes_A_Libldap_Search_With_The_Dex_Filter()
    {
        var search = LdapCodec.Decode(Convert.FromHexString(LibldapSearch)).ShouldBeOfType<LdapSearchRequest>();
        search.MessageId.ShouldBe(2);
        search.BaseDn.ShouldBe("ou=people,dc=imx,dc=sh");
        search.Scope.ShouldBe(LdapScope.WholeSubtree);
        search.Filter.ToString()
            .ShouldBe("(&(&(objectClass=freexUser)(isActive=TRUE)(authorizedService=matrix))(uid=alice))");
        search.Attributes.ShouldBe(["uid", "mail"]);
    }

    [Fact]
    public void Decodes_A_Libldap_Unbind()
        => LdapCodec.Decode(Convert.FromHexString(LibldapUnbind)).ShouldBeOfType<LdapUnbindRequest>();

    [Fact]
    public void Accepts_Non_Minimal_Long_Form_Lengths()
    {
        // The libldap bind with both SEQUENCE lengths rewritten in the 4-byte
        // long form. Legal BER, rejected by a DER reader.
        var original = Convert.FromHexString(LibldapBind);
        var body = original.AsSpan(2).ToArray();                 // messageID + bindRequest
        var op = body.AsSpan(3).ToArray();                       // bindRequest TLV (after 02 01 01)
        var opContent = op.AsSpan(2).ToArray();
        byte[] Long(int n) => [0x84, 0, 0, (byte)(n >> 8), (byte)n];

        var newOp = new byte[] { 0x60 }.Concat(Long(opContent.Length)).Concat(opContent).ToArray();
        var newBody = new byte[] { 0x02, 0x01, 0x01 }.Concat(newOp).ToArray();
        var message = new byte[] { 0x30 }.Concat(Long(newBody.Length)).Concat(newBody).ToArray();

        var bind = LdapCodec.Decode(message).ShouldBeOfType<LdapBindRequest>();
        bind.Name.ShouldBe("cn=dex,ou=services,dc=imx,dc=sh");
        bind.SimplePassword.ShouldBe("Service12345");
    }

    [Fact]
    public async Task Frames_Consecutive_Messages_Off_A_Stream()
    {
        var bytes = Convert.FromHexString(LibldapBind + LibldapSearch + LibldapUnbind);
        using var stream = new MemoryStream(bytes);
        (await LdapCodec.ReadFrameAsync(stream, 1 << 20, CancellationToken.None))!.ShouldBe(Convert.FromHexString(LibldapBind));
        (await LdapCodec.ReadFrameAsync(stream, 1 << 20, CancellationToken.None))!.ShouldBe(Convert.FromHexString(LibldapSearch));
        (await LdapCodec.ReadFrameAsync(stream, 1 << 20, CancellationToken.None))!.ShouldBe(Convert.FromHexString(LibldapUnbind));
        (await LdapCodec.ReadFrameAsync(stream, 1 << 20, CancellationToken.None)).ShouldBeNull();   // clean EOF
    }

    [Fact]
    public async Task An_Announced_Length_Over_The_Limit_Is_Refused_Before_Reading_It()
    {
        // 30 84 7f ff ff ff: a 2 GiB message announced in six bytes. Must be
        // refused from the header, not by trying to buffer it.
        using var stream = new MemoryStream([0x30, 0x84, 0x7F, 0xFF, 0xFF, 0xFF]);
        await Should.ThrowAsync<LdapProtocolException>(() => LdapCodec.ReadFrameAsync(stream, 1 << 20, CancellationToken.None));
    }

    [Fact]
    public async Task The_Indefinite_Length_Form_Is_Refused()
    {
        // RFC 4511 §5.1: only the definite form is allowed.
        using var stream = new MemoryStream([0x30, 0x80, 0x02, 0x01, 0x01, 0x00, 0x00]);
        await Should.ThrowAsync<LdapProtocolException>(() => LdapCodec.ReadFrameAsync(stream, 1 << 20, CancellationToken.None));
    }

    [Fact]
    public void Garbage_Is_A_Protocol_Error_Not_A_Crash()
    {
        Should.Throw<LdapProtocolException>(() => LdapCodec.Decode([0x30, 0x03, 0x02, 0x01, 0x01]));            // no protocolOp
        Should.Throw<LdapProtocolException>(() => LdapCodec.Decode([0x30, 0x05, 0x02, 0x01, 0x01, 0x54, 0x00])); // [APPLICATION 20]: no such op
        Should.Throw<LdapProtocolException>(() => LdapCodec.Decode(Convert.FromHexString(LibldapSearch)[..40]));  // truncated
    }

    [Fact]
    public void Write_Operations_Decode_As_Refusable()
    {
        // DelRequest [APPLICATION 10] is primitive (the DN itself).
        var dn = Encoding.UTF8.GetBytes("uid=alice,ou=people,dc=x");
        var msg = new byte[] { 0x30, (byte)(5 + dn.Length), 0x02, 0x01, 0x07, 0x4A, (byte)dn.Length }.Concat(dn).ToArray();
        var req = LdapCodec.Decode(msg).ShouldBeOfType<LdapUnsupportedRequest>();
        req.OpTag.ShouldBe(LdapOp.DelRequest);
        req.MessageId.ShouldBe(7);
    }

    [Fact]
    public void Encodes_A_Bind_Result_Libldap_Can_Read()
    {
        // invalidCredentials (49) on message 1: 30 0c 02 01 01 61 07 0a 01 31 04 00 04 00
        Convert.ToHexString(LdapCodec.Result(1, LdapOp.BindResponse, LdapResult.InvalidCredentials))
            .ShouldBe("300C02010161070A013104000400");
    }

    [Fact]
    public void Encodes_A_Search_Entry_With_Only_The_Selected_Attributes()
    {
        var entry = new LdapEntry("uid=alice,ou=people,dc=x")
            .Add("uid", "alice").Add("mail", "alice@x").Add("memberOf", "cn=g,ou=groups,dc=x");
        var bytes = LdapCodec.SearchEntry(3, entry, new AttributeSelection(["mail"]), typesOnly: false);

        var msg = new AsnReader(bytes, AsnEncodingRules.BER).ReadSequence();
        msg.TryReadInt32(out var id).ShouldBeTrue();
        id.ShouldBe(3);
        var op = msg.ReadSequence(new Asn1Tag(TagClass.Application, LdapOp.SearchResultEntry, isConstructed: true));
        Encoding.UTF8.GetString(op.ReadOctetString()).ShouldBe("uid=alice,ou=people,dc=x");
        var attrs = op.ReadSequence();
        var only = attrs.ReadSequence();
        Encoding.UTF8.GetString(only.ReadOctetString()).ShouldBe("mail");
        Encoding.UTF8.GetString(only.ReadSetOf().ReadOctetString()).ShouldBe("alice@x");
        attrs.HasData.ShouldBeFalse();
    }
}
