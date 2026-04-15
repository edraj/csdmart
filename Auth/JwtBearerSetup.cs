using System.Text;
using Dmart.Config;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dmart.Auth;

public static class JwtBearerSetup
{
    public static IServiceCollection AddDmartAuth(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Bind JwtBearerOptions LAZILY against IOptions<DmartSettings> so the secret
        // resolves AFTER any test/in-memory config sources have been merged.
        // Reading cfg directly here would bake in the config.env value before
        // WebApplicationFactory adds its test overrides.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<DmartSettings>>((bearer, dmartOpts) =>
            {
                var s = dmartOpts.Value;
                if (s.JwtSecret.Length < 32)
                {
                    var logger = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole()).CreateLogger("JwtBearerSetup");
                    logger.LogWarning("JWT secret is shorter than 32 bytes — this is insecure for HS256");
                }
                var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(s.JwtSecret));

                // Only require HTTPS in production; dev/test use HTTP.
                var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                bearer.RequireHttpsMetadata = env != "Development" && env != "Testing";
                bearer.IncludeErrorDetails = env == "Development";
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = s.JwtIssuer,
                    ValidateAudience = true,
                    ValidAudience = s.JwtAudience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    IssuerSigningKeys = new[] { signingKey },
                    // .NET 9+ JsonWebTokenHandler looks keys up by kid; our hand-rolled
                    // JWT has no kid, so we always return the symmetric key directly.
                    IssuerSigningKeyResolver = (_, _, _, _) => new[] { signingKey },
                    NameClaimType = "sub",
                };

                // dmart Python accepts the JWT from EITHER the Authorization header
                // OR the auth_token cookie. Browser clients depend on the cookie.
                bearer.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        if (string.IsNullOrEmpty(ctx.Token))
                        {
                            var fromCookie = ctx.Request.Cookies["auth_token"];
                            if (!string.IsNullOrEmpty(fromCookie))
                                ctx.Token = fromCookie;
                        }
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization();
        return services;
    }
}
