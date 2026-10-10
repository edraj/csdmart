namespace Dmart.Ldap;

// The numbers from RFC 4511 that the directory face uses. Plain constants
// rather than enums: they travel as INTEGER/ENUMERATED on the wire and are
// compared against decoded ints, never switched on exhaustively.
internal static class LdapResult
{
    public const int Success = 0;
    public const int OperationsError = 1;
    public const int ProtocolError = 2;
    public const int TimeLimitExceeded = 3;
    public const int SizeLimitExceeded = 4;
    public const int AuthMethodNotSupported = 7;
    public const int AdminLimitExceeded = 11;
    public const int UnavailableCriticalExtension = 12;
    public const int ConfidentialityRequired = 13;
    public const int NoSuchObject = 32;
    public const int InvalidDnSyntax = 34;
    public const int InappropriateAuthentication = 48;
    public const int InvalidCredentials = 49;
    public const int InsufficientAccessRights = 50;
    public const int Busy = 51;
    public const int Unavailable = 52;
    public const int UnwillingToPerform = 53;
}

// APPLICATION tag numbers of the protocolOp CHOICE. A response to a request is
// always the request's tag + 1 (ModifyRequest 6 → ModifyResponse 7, ...), which
// is how the read-only refusal answers every write without naming each one.
internal static class LdapOp
{
    public const int BindRequest = 0;
    public const int BindResponse = 1;
    public const int UnbindRequest = 2;
    public const int SearchRequest = 3;
    public const int SearchResultEntry = 4;
    public const int SearchResultDone = 5;
    public const int ModifyRequest = 6;
    public const int AddRequest = 8;
    public const int DelRequest = 10;
    public const int ModifyDnRequest = 12;
    public const int CompareRequest = 14;
    public const int AbandonRequest = 16;
    public const int ExtendedRequest = 23;
    public const int ExtendedResponse = 24;
}

internal static class LdapOid
{
    public const string PagedResults = "1.2.840.113556.1.4.319";       // RFC 2696
    public const string WhoAmI = "1.3.6.1.4.1.4203.1.11.3";            // RFC 4532
    public const string StartTls = "1.3.6.1.4.1.1466.20037";           // RFC 4511 §4.14
    public const string PasswordModify = "1.3.6.1.4.1.4203.1.11.1";    // RFC 3062
    public const string NoticeOfDisconnection = "1.3.6.1.4.1.1466.20036";
}

internal enum LdapScope
{
    BaseObject = 0,
    SingleLevel = 1,
    WholeSubtree = 2,
}

internal sealed record LdapControl(string Oid, bool Critical, byte[]? Value);

// One decoded request. Every request carries its message id and controls; the
// subtypes add what their operation needs and nothing else.
internal abstract record LdapRequest(int MessageId, IReadOnlyList<LdapControl> Controls);

internal sealed record LdapBindRequest(
    int MessageId, IReadOnlyList<LdapControl> Controls,
    int Version, string Name, string? SimplePassword, string? SaslMechanism)
    : LdapRequest(MessageId, Controls);

internal sealed record LdapUnbindRequest(int MessageId, IReadOnlyList<LdapControl> Controls)
    : LdapRequest(MessageId, Controls);

internal sealed record LdapSearchRequest(
    int MessageId, IReadOnlyList<LdapControl> Controls,
    string BaseDn, LdapScope Scope, int SizeLimit, int TimeLimit, bool TypesOnly,
    LdapFilter Filter, IReadOnlyList<string> Attributes)
    : LdapRequest(MessageId, Controls);

internal sealed record LdapAbandonRequest(int MessageId, IReadOnlyList<LdapControl> Controls, int TargetId)
    : LdapRequest(MessageId, Controls);

internal sealed record LdapExtendedRequest(
    int MessageId, IReadOnlyList<LdapControl> Controls, string Name, byte[]? Value)
    : LdapRequest(MessageId, Controls);

// Add, modify, delete, modify-DN and compare. The face is read-only, so all it
// needs to know about one of these is which response tag to refuse it with.
internal sealed record LdapUnsupportedRequest(int MessageId, IReadOnlyList<LdapControl> Controls, int OpTag)
    : LdapRequest(MessageId, Controls);

// A message that could not be decoded. Carries the id when it got that far, so
// the error can be addressed to the request rather than to the connection.
public sealed class LdapProtocolException : Exception
{
    public int MessageId { get; }

    public LdapProtocolException(string message, int messageId) : base(message) => MessageId = messageId;

    // The standard constructors (CA1032). The decoder always uses the one above.
    public LdapProtocolException() { }
    public LdapProtocolException(string message) : base(message) { }
    public LdapProtocolException(string message, Exception inner) : base(message, inner) { }
}
