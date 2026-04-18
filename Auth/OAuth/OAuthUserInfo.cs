namespace Dmart.Auth.OAuth;

// Normalized shape of user attributes extracted from any OAuth provider.
// GoogleProvider / FacebookProvider / AppleProvider all produce this; the
// OAuthUserResolver then finds-or-creates a dmart User from it.
public sealed record OAuthUserInfo(
    string Provider,        // "google" | "facebook" | "apple"
    string ProviderId,      // the provider's stable `sub` / user id
    string? Email,
    string? FirstName,
    string? LastName,
    string? PictureUrl);
