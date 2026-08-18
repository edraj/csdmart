using Microsoft.Extensions.DependencyInjection;
using Dmart.Plugins.Native;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

public class NativePluginTests
{
    [Fact]
    public void NativeMarshal_RoundTrips_Utf8_String()
    {
        var original = "Hello, 世界! 🌍";
        var ptr = NativeMarshal.StringToUtf8(original);
        try
        {
            var result = NativeMarshal.Utf8ToString(ptr);
            result.ShouldBe(original);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void NativeMarshal_Utf8ToString_Returns_Empty_For_Null_Ptr()
    {
        NativeMarshal.Utf8ToString(IntPtr.Zero).ShouldBe("");
    }

    [Fact]
    public void NativePluginLoader_Does_Not_Throw()
    {
        // AddNativePlugins should never throw regardless of whether
        // ~/.dmart/plugins/ exists or has valid/invalid plugins
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Should.NotThrow(() => services.AddNativePlugins());
    }

    [Fact]
    public void NativePluginHandle_Load_Throws_On_Missing_File()
    {
        Should.Throw<DllNotFoundException>(() =>
            NativePluginHandle.Load("/nonexistent/path/plugin.so"));
    }

    [Fact]
    public void ScanRoot_Records_A_Failure_When_Config_Has_No_Binary()
    {
        // The quietest way to lose a plugin: the operator writes config.json,
        // the binary is missing or misnamed, and the scan loop simply falls
        // through. Nothing was logged and nothing was reported, so dmart served
        // happily with the plugin absent. It must now be recorded so startup
        // can log it and /info/plugins can report it.
        var root = Path.Combine(Path.GetTempPath(), $"dmart_plugins_{Guid.NewGuid():N}");
        var name = $"ghost_{Guid.NewGuid():N}"[..20];
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.json"), """{"shortname":"ghost"}""");
        try
        {
            var failures = NativePluginLoader.ScanRoot(new ServiceCollection(), root);

            var failure = failures.SingleOrDefault(f => f.Shortname == name);
            failure.ShouldNotBeNull();
            failure!.Reason.ShouldContain("no plugin binary");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ScanRoot_Ignores_A_Directory_With_No_Config()
    {
        // A stray directory is not a broken plugin — only config.json makes it
        // one. Recording it would train operators to ignore the failure list.
        var root = Path.Combine(Path.GetTempPath(), $"dmart_plugins_{Guid.NewGuid():N}");
        var name = $"stray_{Guid.NewGuid():N}"[..20];
        Directory.CreateDirectory(Path.Combine(root, name));
        try
        {
            NativePluginLoader.ScanRoot(new ServiceCollection(), root)
                .ShouldBeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FindSharedLibrary_Returns_Null_For_Empty_Dir()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"dmart_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            NativePluginLoader.FindSharedLibrary(tmpDir, "test").ShouldBeNull();
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void FindSharedLibrary_Finds_Named_So()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"dmart_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var soPath = Path.Combine(tmpDir, "myplugin.so");
        File.WriteAllBytes(soPath, new byte[] { 0 });
        try
        {
            NativePluginLoader.FindSharedLibrary(tmpDir, "myplugin").ShouldBe(soPath);
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }
}
