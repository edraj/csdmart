using Dmart.Models.Api;

namespace Dmart.Client;

// Raised whenever a dmart HTTP call returns status=failed, a non-2xx
// response with a parsable envelope, or a client-side network error.
// Mirrors pydmart.DmartException: wraps the HTTP status code and the
// structured api.Error from the server (or a synthetic one for
// transport-level failures).
public sealed class DmartException : Exception
{
    public int StatusCode { get; }
    public Error Error { get; }

    public DmartException(int statusCode, Error error)
        : base(error.Message)
    {
        StatusCode = statusCode;
        Error = error;
    }

    public override string ToString()
        => $"DmartException[{StatusCode}] type={Error.Type} code={Error.Code}: {Error.Message}";
}
