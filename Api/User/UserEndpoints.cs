namespace Dmart.Api.User;

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUser(this RouteGroupBuilder g)
    {
        AuthHandler.Map(g);
        RegistrationHandler.Map(g);
        ProfileHandler.Map(g);
        OtpHandler.Map(g);
        OAuth.OAuthHandlers.Map(g);
        return g;
    }
}
