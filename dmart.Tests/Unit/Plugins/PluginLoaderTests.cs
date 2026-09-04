using Microsoft.Extensions.DependencyInjection;
using Dmart.Plugins.Native;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// Covers what the plugin scan reports back. The failure list is the only
// channel an operator has for "the plugin you deployed did not load" — startup
// logs it and /info/plugins serves it — so the cases here are mostly about
// which situations must produce a failure and which must not.
public class PluginLoaderTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), $"dmart_plugins_{Guid.NewGuid():N}");

    [Fact]
    public void AddNativePlugins_Does_Not_Throw()
    {
        // Runs during DI registration, before the logger exists. A throw here
        // takes down startup, so it must tolerate ~/.dmart/plugins being
        // absent, empty, or full of junk.
        var services = new ServiceCollection();
        Should.NotThrow(() => services.AddNativePlugins());
    }

    [Fact]
    public void ScanRoot_Records_A_Failure_When_Config_Has_No_Executable()
    {
        // The quietest way to lose a plugin: the operator writes config.json,
        // the binary is missing or misnamed, and the scan loop simply falls
        // through. Nothing was logged and nothing was reported, so dmart served
        // happily with the plugin absent. It must now be recorded so startup
        // can log it and /info/plugins can report it.
        var root = NewRoot();
        var name = $"ghost_{Guid.NewGuid():N}"[..20];
        Directory.CreateDirectory(Path.Combine(root, name));
        File.WriteAllText(Path.Combine(root, name, "config.json"), """{"shortname":"ghost"}""");
        try
        {
            var failure = NativePluginLoader.ScanRoot(new ServiceCollection(), root)
                .SingleOrDefault(f => f.Shortname == name);

            failure.ShouldNotBeNull();
            failure!.Reason.ShouldContain("no plugin executable");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ScanRoot_Names_The_Removal_When_Only_A_So_Is_Present()
    {
        // A deployment that worked before in-process plugins were removed.
        // "no plugin executable found" would be actively misleading there — the
        // binary is sitting right in the directory — so the operator has to be
        // told the mode is gone and what to do instead.
        var root = NewRoot();
        var name = $"legacy_{Guid.NewGuid():N}"[..20];
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.json"), """{"shortname":"legacy"}""");
        File.WriteAllBytes(Path.Combine(dir, $"{name}.so"), new byte[] { 0x7f, 0x45, 0x4c, 0x46 });
        try
        {
            var failure = NativePluginLoader.ScanRoot(new ServiceCollection(), root)
                .SingleOrDefault(f => f.Shortname == name);

            failure.ShouldNotBeNull();
            failure!.Reason.ShouldContain($"{name}.so");
            failure.Reason.ShouldContain("no longer");
            failure.Reason.ShouldContain("subprocess");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ScanRoot_Ignores_A_Directory_With_No_Config()
    {
        // A stray directory is not a broken plugin — only config.json makes it
        // one. Recording it would train operators to ignore the failure list.
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, $"stray_{Guid.NewGuid():N}"[..20]));
        try
        {
            NativePluginLoader.ScanRoot(new ServiceCollection(), root).ShouldBeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FindSharedLibrary_Detects_A_Legacy_So_And_Nothing_Else()
    {
        // Retained purely so ScanRoot can recognise a pre-removal deployment.
        // Nothing loads what it finds.
        var dir = NewRoot();
        Directory.CreateDirectory(dir);
        try
        {
            NativePluginLoader.FindSharedLibrary(dir, "myplugin").ShouldBeNull();

            var soPath = Path.Combine(dir, "myplugin.so");
            File.WriteAllBytes(soPath, new byte[] { 0 });
            NativePluginLoader.FindSharedLibrary(dir, "myplugin").ShouldBe(soPath);
        }
        finally { Directory.Delete(dir, true); }
    }
}
