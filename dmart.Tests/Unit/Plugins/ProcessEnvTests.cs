using System.Runtime.InteropServices;
using Dmart.Plugins.Native;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// Pins the regression this fixes: Environment.SetEnvironmentVariable alone
// only updates .NET's managed cache and is invisible to the real process
// environ, which is what an in-process NativeAOT plugin's embedded runtime
// reads. ProcessEnv.SetAtStartup must additionally reach the real environ.
//
// Unix-only by construction: both P/Invokes below bind to libc, which does not
// resolve on Windows. Nothing is lost by skipping there — ProcessEnv skips the
// native write on Windows too, because the managed API already updates the
// Win32 environment block that a plugin would read.
public sealed partial class ProcessEnvTests
{
    [LibraryImport("libc", EntryPoint = "getenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetEnvNative(string name);

    // Undo for the tests below. Environment.SetEnvironmentVariable(name, null)
    // does NOT do this job — it clears the managed cache only, which is the
    // entire premise of ProcessEnv, so using it as cleanup would leave the
    // variable in the real environ for every later test in this process.
    [LibraryImport("libc", EntryPoint = "unsetenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnsetEnvNative(string name);

    private static string? NativeGet(string name)
        => Marshal.PtrToStringUTF8(GetEnvNative(name));

    private static void Cleanup(string name)
    {
        Environment.SetEnvironmentVariable(name, null);
        if (!OperatingSystem.IsWindows()) UnsetEnvNative(name);
    }

    [Fact]
    public void Set_Is_Visible_To_Native_Getenv()
    {
        if (OperatingSystem.IsWindows()) return;

        var name = $"DMART_TEST_{Guid.NewGuid():N}";
        try
        {
            ProcessEnv.SetAtStartup(name, "hello");

            NativeGet(name).ShouldBe("hello");
        }
        finally
        {
            Cleanup(name);
        }
    }

    // The other half of the story, and the reason ProcessEnv exists at all: the
    // managed API on its own never reaches the real environ. Without this, the
    // test above passes whether or not the libc write is still needed — if a
    // future .NET starts writing through, this is the assertion that flips and
    // tells us the P/Invoke has become dead weight.
    [Fact]
    public void Managed_Set_Alone_Is_Invisible_To_Native_Getenv()
    {
        if (OperatingSystem.IsWindows()) return;

        var name = $"DMART_TEST_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(name, "managed-only");

            Environment.GetEnvironmentVariable(name).ShouldBe("managed-only",
                "the managed cache must still see its own write");
            NativeGet(name).ShouldBeNull(
                "if this now returns a value, .NET has started writing through to "
                + "libc and ProcessEnv's setenv call is redundant");
        }
        finally
        {
            Cleanup(name);
        }
    }

    // Cleanup has to actually clean up, or every run of this class leaves a
    // DMART_TEST_<guid> behind in the process environ.
    [Fact]
    public void Cleanup_Removes_The_Variable_From_The_Real_Environ()
    {
        if (OperatingSystem.IsWindows()) return;

        var name = $"DMART_TEST_{Guid.NewGuid():N}";
        ProcessEnv.SetAtStartup(name, "hello");
        Cleanup(name);

        NativeGet(name).ShouldBeNull();
        Environment.GetEnvironmentVariable(name).ShouldBeNull();
    }
}
