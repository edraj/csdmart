using Dmart.Models.Api;
using Dmart.Models.Json;

namespace Dmart.Api.Qr;

public static class ValidateHandler
{
    // 501, not a verdict. QrService is a stub — ValidateAsync returns `true`
    // for any input — so this anonymous route used to rubber-stamp every
    // payload handed to it, and any client trusting it accepted forged QR
    // codes. Answering "not implemented" is the only honest response until
    // there is a real validator.
    //
    // When QrService is actually implemented, this route must also gain
    // `.RequireAuthorization()` and a `perms.CanReadAsync(actor, locator, ...)`
    // gate on the locator encoded in the payload — validating a QR code for a
    // resource the caller can't read is itself a disclosure.
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/validate", () => Results.Json(
            Response.Fail(InternalErrorCode.QR_ERROR,
                "qr validation is not implemented", ErrorTypes.Qr),
            DmartJsonContext.Default.Response,
            statusCode: StatusCodes.Status501NotImplemented));
}
