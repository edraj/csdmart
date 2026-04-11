using Dmart.Api.Info;
using Dmart.Api.Managed;
using Dmart.Api.Public;
using Dmart.Api.Qr;
using Dmart.Api.User;
using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Middleware;
using Dmart.Models.Json;
using Dmart.Plugins;
using Dmart.Services;

// dmart Python uses `timestamp without time zone` columns. Npgsql 6+ rejects
// DateTime values with Kind=Utc against those columns unless this switch is set.
// This MUST run before any Npgsql operation, so it lives at the very top.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateSlimBuilder(args);

// AOT-friendly JSON: source-generated context. We REPLACE the default resolver
// (instead of inserting at position 0) so the framework's camelCase default policy
// can't override the snake_case policy declared on DmartJsonContext.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolver = DmartJsonContext.Default;
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
});

// Config
builder.Services.Configure<DmartSettings>(builder.Configuration.GetSection("Dmart"));
builder.Services.Configure<SpacesOptions>(builder.Configuration.GetSection("Spaces"));

// SQL backend
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<AuthzCacheRefresher>();
builder.Services.AddSingleton<EntryRepository>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<AccessRepository>();
builder.Services.AddSingleton<AttachmentRepository>();
builder.Services.AddSingleton<HistoryRepository>();
builder.Services.AddSingleton<LockRepository>();
builder.Services.AddSingleton<LinkRepository>();
builder.Services.AddSingleton<OtpRepository>();
builder.Services.AddSingleton<CountHistoryRepository>();
builder.Services.AddSingleton<HealthCheckRepository>();
builder.Services.AddSingleton<SpaceRepository>();
builder.Services.AddHostedService<CountHistorySnapshotter>();

// Schema bootstrapper runs once on startup. AdminBootstrap MUST be registered AFTER
// SchemaInitializer — IHostedServices run StartAsync sequentially in registration order.
builder.Services.AddHostedService<SchemaInitializer>();
builder.Services.AddHostedService<AdminBootstrap>();

// Domain services
builder.Services.AddSingleton<PermissionService>();
builder.Services.AddSingleton<SchemaValidator>();
builder.Services.AddSingleton<EntryService>();
builder.Services.AddSingleton<QueryService>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<WorkflowEngine>();
builder.Services.AddSingleton<WorkflowService>();
builder.Services.AddSingleton<EventBus>();
builder.Services.AddSingleton<PluginHost>();
builder.Services.AddSingleton<LockService>();
builder.Services.AddSingleton<ShortLinkService>();
builder.Services.AddSingleton<CsvService>();
builder.Services.AddSingleton<ImportExportService>();
builder.Services.AddSingleton<QrService>();

// Auth
builder.Services.AddSingleton<JwtIssuer>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<OtpProvider>();
builder.Services.AddSingleton<Dmart.Auth.OAuth.GoogleProvider>();
builder.Services.AddSingleton<Dmart.Auth.OAuth.FacebookProvider>();
builder.Services.AddSingleton<Dmart.Auth.OAuth.AppleProvider>();
builder.Services.AddDmartAuth(builder.Configuration);

// Plugins
builder.Services.AddDmartPlugins();

// Per-request context
builder.Services.AddScoped<RequestContext>();

var app = builder.Build();

// Catches unhandled exceptions from any handler and maps them to a Response.Fail 500.
// Inline so we don't depend on UseMiddleware<T> reflection (AOT-friendly). Must be
// first in the pipeline so it covers auth + endpoint exceptions.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            ctx.Response.ContentType = "application/json";
            var body = Dmart.Models.Api.Response.Fail("internal_error", ex.Message, "exception");
            await ctx.Response.WriteAsJsonAsync(body, DmartJsonContext.Default.Response);
        }
    }
});

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "dmart-csharp");

app.MapGroup("/managed").RequireAuthorization().MapManaged();
app.MapGroup("/public").MapPublic();
app.MapGroup("/user").MapUser();
app.MapGroup("/info").RequireAuthorization().MapInfo();
app.MapGroup("/qr").MapQr();

app.Run();

// Exposed so dmart.Tests can use WebApplicationFactory<Program>.
public partial class Program;
