namespace Dmart.Api.Public;

public static class PublicEndpoints
{
    public static RouteGroupBuilder MapPublic(this RouteGroupBuilder g)
    {
        QueryHandler.Map(g);
        EntryHandler.Map(g);
        PayloadHandler.Map(g);
        SubmitHandler.Map(g);
        AttachHandler.Map(g);
        ExecuteTaskHandler.Map(g);
        return g;
    }
}
