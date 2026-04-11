using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;

namespace Dmart.Api.User;

public static class OtpHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/otp-request", async (SendOTPRequest req, OtpProvider otp, OtpRepository repo, CancellationToken ct) =>
        {
            var dest = req.Msisdn ?? req.Email ?? "";
            if (string.IsNullOrEmpty(dest)) return Response.Fail("bad_request", "destination required");
            var code = otp.Generate();
            await repo.StoreAsync(dest, code, DateTime.UtcNow.AddMinutes(10), ct);
            await otp.SendAsync(dest, code, ct);
            return Response.Ok();
        });

        g.MapPost("/otp-request-login", async (SendOTPRequest req, OtpProvider otp, OtpRepository repo, CancellationToken ct) =>
        {
            var dest = req.Msisdn ?? req.Email ?? "";
            var code = otp.Generate();
            await repo.StoreAsync($"login:{dest}", code, DateTime.UtcNow.AddMinutes(5), ct);
            return Response.Ok();
        });

        g.MapPost("/password-reset-request", async (PasswordResetRequest req, OtpProvider otp, OtpRepository repo, CancellationToken ct) =>
        {
            var dest = req.Email ?? req.Msisdn ?? req.Shortname ?? "";
            var code = otp.Generate();
            await repo.StoreAsync($"reset:{dest}", code, DateTime.UtcNow.AddMinutes(15), ct);
            return Response.Ok();
        });

        g.MapPost("/otp-confirm", async (ConfirmOTPRequest req, OtpRepository repo, CancellationToken ct) =>
        {
            var dest = req.Msisdn ?? req.Email ?? "";
            var ok = await repo.VerifyAndConsumeAsync(dest, req.Code, ct);
            return ok ? Response.Ok() : Response.Fail("invalid_otp", "code mismatch or expired");
        });
    }
}
