namespace Dmart.Utils;

// Lightweight Result<T> for service-layer outcomes. Failure carries the exact
// wire triple: integer InternalErrorCode, human-readable message, and type
// string ("auth", "db", "request", "jwtauth", "qr", "internal", …).
//
// Matches Python's `api.Exception(..., error=api.Error(type=..., code=..., message=...))`
// so the HTTP response can emit the triple verbatim. There is no string-name
// shortcut — every caller supplies the integer constant and type explicitly.
//
// Info optionally carries structured failure detail in the wire Error.Info
// shape (list of dicts, mirroring Python's api.Error.info) — e.g. per-field
// schema-validation errors from SchemaValidator.ToInfo — so consumers can act
// on the failure without parsing ErrorMessage.
public readonly record struct Result<T>(
    bool IsOk,
    T? Value,
    int ErrorCode,
    string? ErrorMessage,
    string? ErrorType,
    List<Dictionary<string, object>>? Info = null)
{
    public static Result<T> Ok(T value) => new(true, value, 0, null, null);

    public static Result<T> Fail(int code, string message, string type,
        List<Dictionary<string, object>>? info = null) =>
        new(false, default, code, message, type, info);
}
