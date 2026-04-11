namespace Dmart.Utils;

// Lightweight Result<T,E> for service-layer outcomes (avoids exceptions for expected failures).
public readonly record struct Result<T>(bool IsOk, T? Value, string? ErrorCode, string? ErrorMessage)
{
    public static Result<T> Ok(T value) => new(true, value, null, null);
    public static Result<T> Fail(string code, string message) => new(false, default, code, message);
}
