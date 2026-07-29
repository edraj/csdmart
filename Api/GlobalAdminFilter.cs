using Dmart.Models.Api;
using Dmart.Services;

namespace Dmart.Api;

// Endpoint filter that gates an entire route group to effective super-admins,
// mirroring the per-handler IsGlobalAdminAsync checks in
// Api/Managed/ImportExportHandler.cs — for groups where every route needs the
// same super-admin floor rather than case-by-case checks in each handler.
public sealed class GlobalAdminFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var perms = http.RequestServices.GetRequiredService<PermissionService>();

        if (!await perms.IsGlobalAdminAsync(http.Actor(), http.RequestAborted))
            return Response.Fail(InternalErrorCode.NOT_ALLOWED,
                "not allowed — global admin required", ErrorTypes.Request);

        return await next(context);
    }
}
