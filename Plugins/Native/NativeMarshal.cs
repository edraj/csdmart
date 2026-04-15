using System.Runtime.InteropServices;
using System.Text;

namespace Dmart.Plugins.Native;

// UTF-8 string marshalling between managed C# and unmanaged char*.
internal static class NativeMarshal
{
    public static unsafe IntPtr StringToUtf8(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        ((byte*)ptr)[bytes.Length] = 0;
        return ptr;
    }

    public static string Utf8ToString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return "";
        return Marshal.PtrToStringUTF8(ptr) ?? "";
    }
}
