using Dmart.Models.Api;
using Dmart.Services;

namespace Dmart.Api;

// Marks a route inside an admin-gated group as needing only authentication.
//
// An explicit, greppable marker rather than a path check inside the filter,
// and the filter fails CLOSED — an endpoint is super-admin-only unless it
// carries this — so a route added to such a group can never accidentally
// inherit the weaker rule. Granting the exemption is a visible edit at the
// route's own map call, which is where a reviewer is looking.
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowAuthenticatedAttribute : Attribute;

// Endpoint filter that gates an entire route group to effective super-admins,
// mirroring the per-handler IsGlobalAdminAsync checks in
// Api/Managed/ImportExportHandler.cs — for groups where every route needs the
// same super-admin floor rather than case-by-case checks in each handler.
//
// Authentication itself is not this filter's job: the groups it guards carry
// RequireAuthorization(), so an anonymous caller is already rejected with a 401
// before any of this runs. What this adds is the role floor on top of that.
public sealed class GlobalAdminFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // Opt-out for routes that only need a signed-in caller — see
        // AllowAuthenticatedAttribute. Checked before resolving permissions, so
        // an exempt route costs no lookup.
        if (http.GetEndpoint()?.Metadata.GetMetadata<AllowAuthenticatedAttribute>() is not null)
            return await next(context);

        var perms = http.RequestServices.GetRequiredService<PermissionService>();

        if (!await perms.IsGlobalAdminAsync(http.Actor(), http.RequestAborted))
            return Response.Fail(InternalErrorCode.NOT_ALLOWED,
                "not allowed — global admin required", ErrorTypes.Request);

        return await next(context);
    }
}
