namespace Dmart.Models.Api;

public sealed record UserLoginRequest(
    string? Shortname,
    string? Email,
    string? Msisdn,
    string? Password,
    // OTP code — when non-null, LoginWithOtpAsync is used instead of password auth.
    string? Otp = null,
    // Mobile clients: device identifier + push token.
    string? DeviceId = null,
    string? FirebaseToken = null);

// The closed set of OTP purposes. Purpose is part of an OTP's identity —
// a code issued for one purpose can never be redeemed under another (a
// phished password-reset code must not double as a login credential), and
// each (identifier, purpose) pair holds its own independent live code.
public static class OtpPurpose
{
    public const string Login = "login";
    public const string Reset = "reset";
    // Anonymous signup verification — gated by is_registrable + the enabled
    // registration channels; consumed by /user/create.
    public const string Register = "register";
    // Authenticated profile confirm/change — JWT required; consumed by
    // /user/profile.
    public const string VerifyContact = "verify-contact";

    public static bool IsValid(string? purpose)
        => purpose is Login or Reset or Register or VerifyContact;
}

// POST /user/otp-request — the single OTP issuing API. Purpose selects what
// the code will be redeemable for (see OtpPurpose); exactly one of
// {Msisdn, Email, Shortname} identifies the destination/user.
public sealed record SendOTPRequest(
    string? Msisdn,
    string? Email,
    string? Shortname = null,
    string? Purpose = null);

// POST /user/password-reset-confirm. Identifier is one of {Shortname,
// Email, Msisdn}, resolving to the same user as the /user/otp-request
// purpose=reset call that issued Otp. Password is hashed server-side via
// PasswordHasher.Hash.
public sealed record PasswordResetConfirm(
    string? Shortname,
    string? Email,
    string? Msisdn,
    string Otp,
    string Password);

// /user/create — self-registration. Deliberately omits `shortname` and
// `uuid`: the server allocates both so that anonymous callers cannot
// squat on names or pre-empt identifiers. `attributes` carries the
// usual user payload (email, msisdn, password, OTPs, displayname, etc.).
// Other top-level fields a caller might send (resource_type, subpath,
// shortname, uuid) are accepted-and-ignored as unknown JSON properties
// — the resource type is always "user" and the subpath is always
// "/users" on this endpoint.
public sealed record UserCreateBody(
    Dictionary<string, object>? Attributes);
// RFC 7591 dynamic client registration — MCP clients post this to /oauth/register
// and we echo back a clients_id they use for the authorize+token flow.
public sealed record RegisterRequest(
    List<string>? RedirectUris,
    string? ClientName,
    // Fields the spec allows clients to send but we don't honor on public
    // clients — accepted for compatibility with Claude Desktop / Cursor
    // registration bodies, but ignored.
    string? TokenEndpointAuthMethod = null,
    List<string>? GrantTypes = null,
    List<string>? ResponseTypes = null);

public sealed record RegisterResponse(
    string ClientId,
    string ClientName,
    List<string> RedirectUris,
    string TokenEndpointAuthMethod,
    List<string> GrantTypes,
    List<string> ResponseTypes);
