using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Utils;

namespace Dmart.Services;

public sealed class UserService(UserRepository users, PasswordHasher hasher, JwtIssuer jwt)
{
    private const string MgmtSpace = "management";

    public Task<User?> GetByShortnameAsync(string shortname, CancellationToken ct = default)
        => users.GetByShortnameAsync(shortname, ct);

    public async Task<Result<User>> CreateAsync(string shortname, string? email, string? msisdn, string? password, string? language, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(shortname))
            return Result<User>.Fail("invalid_shortname", "shortname required");
        if (await users.ExistsAsync(shortname, email, msisdn, ct))
            return Result<User>.Fail("conflict", "user already exists");

        var user = new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = MgmtSpace,
            Subpath = "users",
            OwnerShortname = shortname,
            Email = email,
            Msisdn = msisdn,
            Password = string.IsNullOrEmpty(password) ? null : hasher.Hash(password),
            Language = ParseLanguage(language),
            Type = UserType.Web,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(user, ct);
        return Result<User>.Ok(user);
    }

    public async Task<Result<(string Access, string Refresh, User User)>> LoginAsync(UserLoginRequest req, CancellationToken ct = default)
    {
        var user = req.Shortname is not null ? await users.GetByShortnameAsync(req.Shortname, ct)
                 : req.Email is not null     ? await users.GetByEmailAsync(req.Email, ct)
                 : req.Msisdn is not null    ? await users.GetByMsisdnAsync(req.Msisdn, ct)
                 : null;
        if (user is null)
            return Result<(string, string, User)>.Fail("not_found", "user not found");
        if (!user.IsActive)
            return Result<(string, string, User)>.Fail("inactive", "user is inactive");
        if (string.IsNullOrEmpty(user.Password) || req.Password is null
            || !hasher.Verify(req.Password, user.Password))
        {
            await users.IncrementAttemptAsync(user.Shortname, ct);
            return Result<(string, string, User)>.Fail("invalid_credentials", "incorrect password");
        }
        await users.ResetAttemptsAsync(user.Shortname, ct);
        var access = jwt.IssueAccess(user.Shortname, user.Roles);
        var refresh = jwt.IssueRefresh(user.Shortname);
        return Result<(string, string, User)>.Ok((access, refresh, user));
    }

    public async Task<Result<User>> UpdateProfileAsync(string shortname, Dictionary<string, object> patch, CancellationToken ct = default)
    {
        var user = await users.GetByShortnameAsync(shortname, ct);
        if (user is null) return Result<User>.Fail("not_found", "user missing");
        var updated = user with
        {
            Email = patch.TryGetValue("email", out var e) ? e?.ToString() : user.Email,
            Msisdn = patch.TryGetValue("msisdn", out var m) ? m?.ToString() : user.Msisdn,
            Language = patch.TryGetValue("language", out var l) && l is not null
                ? ParseLanguage(l.ToString())
                : user.Language,
            Displayname = patch.TryGetValue("displayname", out var dn) && dn is not null
                ? new Translation(En: dn.ToString())
                : user.Displayname,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(updated, ct);
        return Result<User>.Ok(updated);
    }

    public Task DeleteAsync(string shortname, CancellationToken ct = default)
        => users.DeleteAsync(shortname, ct);

    private static Language ParseLanguage(string? code) => code?.ToLowerInvariant() switch
    {
        "ar" or "arabic"  => Language.Ar,
        "ku" or "kurdish" => Language.Ku,
        "fr" or "french"  => Language.Fr,
        "tr" or "turkish" => Language.Tr,
        _                 => Language.En,
    };
}
