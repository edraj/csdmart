using Dmart.Models.Api;
using Dmart.Models.Json;

namespace Dmart.Api.Qr;

public static class GenerateHandler
{
    // 501 for the same reason as ValidateHandler: QrService.GenerateAsync is a
    // stub that returns an empty byte[], so this anonymous route served a
    // zero-byte "image/png" for any locator a caller named.
    //
    // When QrService is actually implemented, this route must also gain
    // `.RequireAuthorization()` and a `perms.CanReadAsync(actor, locator, ...)`
    // gate — a QR code encodes the entry it points at, so minting one for an
    // arbitrary locator leaks the same thing reading the entry would.
    public static void Map(RouteGroupBuilder g) =>
        g.MapGet("/generate/{resource_type}/{space}/{**rest}", () => Results.Json(
            Response.Fail(InternalErrorCode.QR_ERROR,
                "qr generation is not implemented", ErrorTypes.Qr),
            DmartJsonContext.Default.Response,
            statusCode: StatusCodes.Status501NotImplemented));
}
