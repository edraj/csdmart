namespace Dmart.Models.Api;

// Raised whenever a dmart operation fails — an HTTP call returning status=failed
// / non-2xx (Dmart.Client), or a failed direct-DB operation (Dmart.SqlAdapter).
// Base of the typed hierarchy so callers can `catch (DmartException)` for any
// failure or catch a specific subtype. Wraps the HTTP-equivalent status code
// and the structured api.Error triple.
public class DmartException : Exception
{
    public int StatusCode { get; }
    public Error Error { get; }

    public DmartException(int statusCode, Error error)
        : this(statusCode, error, null) { }

    public DmartException(int statusCode, Error error, Exception? innerException)
        : base(error.Message, innerException)
    {
        StatusCode = statusCode;
        Error = error;
    }

    public override string ToString()
        => $"{GetType().Name}[{StatusCode}] type={Error.Type} code={Error.Code}: {Error.Message}";
}
