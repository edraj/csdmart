using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;

namespace Dmart.Services;

public sealed class LockService(LockRepository locks)
{
    public async Task<Response> LockAsync(Locator l, string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(actor)) return Response.Fail("unauthorized", "login required");
        var ok = await locks.TryLockAsync(l.SpaceName, l.Subpath, l.Shortname, actor, ct);
        return ok
            ? Response.Ok(attributes: new() { ["locked_by"] = actor })
            : Response.Fail("locked", $"already locked by {await locks.GetLockerAsync(l.SpaceName, l.Subpath, l.Shortname, ct)}");
    }

    public async Task<Response> UnlockAsync(Locator l, string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(actor)) return Response.Fail("unauthorized", "login required");
        var ok = await locks.UnlockAsync(l.SpaceName, l.Subpath, l.Shortname, actor, ct);
        return ok ? Response.Ok() : Response.Fail("not_owner", "you don't hold this lock");
    }

    public Task<string?> GetLockerAsync(Locator l, CancellationToken ct = default)
        => locks.GetLockerAsync(l.SpaceName, l.Subpath, l.Shortname, ct);
}
