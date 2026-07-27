using System.Runtime.InteropServices;
using Dmart.Plugins.Native;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// Pins the regression this fixes: Environment.SetEnvironmentVariable alone
// only updates .NET's managed cache and is invisible to the real process
// environ, which is what an in-process NativeAOT plugin's embedded runtime
// reads. ProcessEnv.Set must additionally reach the real environ.
public sealed partial class ProcessEnvTests
{
    [LibraryImport("libc", EntryPoint = "getenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetEnvNative(string name);

    [Fact]
    public void Set_Is_Visible_To_Native_Getenv()
    {
        var name = $"DMART_TEST_{Guid.NewGuid():N}";
        try
        {
            ProcessEnv.Set(name, "hello");

            Marshal.PtrToStringUTF8(GetEnvNative(name)).ShouldBe("hello");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
