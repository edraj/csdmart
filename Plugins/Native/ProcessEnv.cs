using System.Runtime.InteropServices;

namespace Dmart.Plugins.Native;

// On Unix, Environment.SetEnvironmentVariable only mutates this runtime's
// managed environment cache — it never calls libc setenv, so the real
// process environ is left untouched. In-process NativeAOT plugins carry
// their own embedded .NET runtime whose environment view comes from the
// real environ, so a variable set via the managed API alone is invisible
// to them. Write through to libc so host, subprocess plugins (which
// inherit the managed view via ProcessStartInfo), and in-process .so
// plugins all agree.
internal static partial class ProcessEnv
{
    [LibraryImport("libc", EntryPoint = "setenv",
        StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    // System32 is a Windows-only search-path value; on Unix this attribute
    // is a no-op and is present solely to satisfy the CA5392 analyzer.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetEnvNative(string name, string value, int overwrite);

    // Call during single-threaded startup only: setenv(3) is not
    // thread-safe against concurrent getenv from other threads.
    public static void Set(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        if (!OperatingSystem.IsWindows() && SetEnvNative(name, value, 1) != 0)
            Console.Error.WriteLine(
                $"ProcessEnv.Set: setenv({name}) failed (errno={Marshal.GetLastPInvokeError()})");
    }
}
