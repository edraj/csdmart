using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Api;

// Mirrors dmart's models/api.py::Response. Wire format:
//   { "status": "success" | "failed",
//     "error":  { "type": "...", "code": <int>, "message": "...", "info": [{}] } | null,
//     "records": [...] | null,
//     "attributes": {...} | null }
public sealed record Response
{
    public Status Status { get; init; } = Status.Success;
    public Error? Error { get; init; }
    public List<Record>? Records { get; init; }
    public Dictionary<string, object>? Attributes { get; init; }

    public static Response Ok(IEnumerable<Record>? records = null, Dictionary<string, object>? attributes = null)
        => new() { Status = Status.Success, Records = records?.ToList(), Attributes = attributes };

    public static Response Fail(int code, string message, string type = "internal", List<Dictionary<string, object>>? info = null)
        => new() { Status = Status.Failed, Error = new Error(type, code, message, info) };

    // Convenience for the common "string-named" code paths. Maps a few well-known
    // names to dmart's InternalErrorCode integers; falls back to SOMETHING_WRONG.
    public static Response Fail(string code, string message, string type = "internal", List<Dictionary<string, object>>? info = null)
        => Fail(MapNameToCode(code), message, type, info);

    private static int MapNameToCode(string code) => code switch
    {
        "not_found"           => InternalErrorCode.SHORTNAME_DOES_NOT_EXIST,
        "forbidden"           => InternalErrorCode.NOT_ALLOWED,
        "unauthorized"        => InternalErrorCode.NOT_AUTHENTICATED,
        "conflict"            => InternalErrorCode.CONFLICT,
        "bad_request"         => InternalErrorCode.INVALID_DATA,
        "internal_error"      => InternalErrorCode.SOMETHING_WRONG,
        "not_implemented"     => InternalErrorCode.NOT_SUPPORTED_TYPE,
        "locked"              => InternalErrorCode.LOCKED_ENTRY,
        "not_owner"           => InternalErrorCode.NOT_ALLOWED,
        "invalid_credentials" => InternalErrorCode.INVALID_USERNAME_AND_PASS,
        "inactive"            => InternalErrorCode.USER_ACCOUNT_LOCKED,
        "invalid_otp"         => InternalErrorCode.OTP_INVALID,
        "invalid_qr"          => InternalErrorCode.QR_INVALID,
        "bad_query"           => InternalErrorCode.INVALID_DATA,
        "bad_type"            => InternalErrorCode.NOT_SUPPORTED_TYPE,
        "invalid_shortname"   => InternalErrorCode.INVALID_IDENTIFIER,
        _                     => InternalErrorCode.SOMETHING_WRONG,
    };
}

// Mirrors dmart's models/enums.py::Status — StrEnum with values "success"/"failed".
[JsonConverter(typeof(StatusJsonConverter))]
public enum Status
{
    [EnumMember(Value = "success")] Success,
    [EnumMember(Value = "failed")]  Failed,
}

public sealed class StatusJsonConverter : EnumMemberConverterBase<Status> { }

// Mirrors dmart's models/api.py::Error — note `code` is an INT.
public sealed record Error(
    string Type,
    int Code,
    string Message,
    List<Dictionary<string, object>>? Info);
