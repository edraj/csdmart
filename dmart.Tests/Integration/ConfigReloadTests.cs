using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// dmart is a production microservice: configuration is read ONCE at boot.
//
// reloadOnChange=true on the host's default appsettings.json sources puts an
// inotify FileSystemWatcher on the CONTENT ROOT directory — even when the
// files don't exist, because the watcher waits for them to appear. Every
// write under that directory (a log line, an upload) then fires the watcher
// callback chain (lstat/read/filter on one thread); under a hot request load
// that burns a core, churns the GC, and starves the request pool.
//
// Program.cs suppresses this via the host setting
// `hostBuilder:reloadConfigOnChange=false` (set as a DOTNET_ env var before
// the builder is created, so the watchers are never constructed at all).
// This test pins the suppression IN PLACE: it inspects the configuration
// actually built by the booted app and fails if any file-based source is
// back to watching.
public class ConfigReloadTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ConfigReloadTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public void No_File_Configuration_Source_Watches_For_Changes()
    {
        var root = (IConfigurationRoot)_factory.Services.GetRequiredService<IConfiguration>();

        var fileProviders = root.Providers.OfType<FileConfigurationProvider>().ToList();
        // The slim builder always adds the two optional appsettings.json
        // sources. If this list is ever empty the loop below passes
        // vacuously, so pin the premise as well.
        fileProviders.ShouldNotBeEmpty(
            "expected the default appsettings JSON sources to be registered");

        foreach (var provider in fileProviders)
            provider.Source.ReloadOnChange.ShouldBeFalse(
                $"config source '{provider.Source.Path}' must not watch for file changes — " +
                "config is read once at boot (see hostBuilder:reloadConfigOnChange in Program.cs)");
    }
}
