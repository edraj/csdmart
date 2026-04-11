namespace Dmart.Models.Api;

public sealed record UserLoginRequest(
    string? Shortname,
    string? Email,
    string? Msisdn,
    string? Password,
    string? InvitationToken,
    // Mirrors dmart Python's /user/login body — mobile clients send their
    // device identifier so the server can (1) reject cross-device logins for
    // locked accounts and (2) persist the latest device_id on the user row.
    string? DeviceId = null,
    string? FirebaseToken = null);

public sealed record SendOTPRequest(
    string? Msisdn,
    string? Email);

public sealed record ConfirmOTPRequest(
    string Code,
    string? Msisdn,
    string? Email);

public sealed record PasswordResetRequest(
    string? Shortname,
    string? Email,
    string? Msisdn);
