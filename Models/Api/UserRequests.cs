namespace Dmart.Models.Api;

public sealed record UserLoginRequest(
    string? Shortname,
    string? Email,
    string? Msisdn,
    string? Password,
    string? InvitationToken);

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
