using Dmart.Plugins.BuiltIn;

namespace Dmart.Plugins;

// AOT-safe: no Assembly.LoadFrom. Built-in plugins are registered explicitly.
public static class PluginLoader
{
    public static IServiceCollection AddDmartPlugins(this IServiceCollection services)
    {
        services.AddSingleton<IPlugin, NotificationPlugin>();
        services.AddSingleton<IPlugin, WebhookPlugin>();
        services.AddSingleton<IPlugin, AuditPlugin>();
        return services;
    }
}
