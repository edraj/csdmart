using Dmart.Models.Api;

namespace Dmart.Api.Public;

public static class ExecuteTaskHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/excute/{task_type}/{space_name}",
            (string task_type, string space_name) => Response.Fail("not_implemented", "public task pending"));
}
