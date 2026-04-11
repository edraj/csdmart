namespace Dmart.Api.Qr;

public static class QrEndpoints
{
    public static RouteGroupBuilder MapQr(this RouteGroupBuilder g)
    {
        GenerateHandler.Map(g);
        ValidateHandler.Map(g);
        return g;
    }
}
