using System.Runtime.InteropServices;

namespace Dmart.Auth;

/// <summary>
/// Argon2id from the reference C library (<c>libargon2</c>).
/// </summary>
/// <remarks>
/// Replaces the managed Konscious implementation, for two measured reasons —
/// both about what Argon2's working buffer does to a .NET process rather than
/// about the algorithm, which is identical either way.
///
/// **The buffer stops being the GC's problem.** Argon2 needs `m` KiB of scratch
/// for the duration of one call. Managed, that is an allocation well past the
/// 85 KB Large Object Heap threshold, so every hash hands the GC a 19 MiB (or,
/// for a legacy hash, 100 MiB) object with a lifetime of milliseconds.
/// Measured on an 8-core x86-64 host, five sequential hashes:
///
///     m=19456    managed: RSS 23 -> 81 MB,  2 gen2 GCs   native: 23 -> 24 MB, 0 GCs
///     m=102400   managed: RSS 27 -> 355 MB, 3 gen2 GCs   native: 27 -> 27 MB, 0 GCs
///
/// The managed memory is not leaked — a forced collection returns it — but "the
/// GC will get to it" is the wrong property on a 430 MB board, and it is why
/// PasswordHasher's budget limiter under-reports real RSS: the limiter bounds
/// what it admits, while buffers from finished hashes are still resident.
/// Native malloc/free returns the memory when the call ends, so the budget and
/// the resident set agree.
///
/// **It is faster, most at the parameters we now default to.** 45.8 ms -> 14.9 ms
/// (3.07x) at m=19456/t=2/p=1. The gap narrows to 1.22x at the legacy
/// m=102400/t=3/p=8, where the managed implementation gets to spread eight lanes
/// across eight cores.
///
/// Output is byte-identical to Konscious at both parameter sets, verified
/// before the swap — which is what makes this a drop-in rather than a
/// migration. libsodium's crypto_pwhash also matches at p=1, but its API cannot
/// express p at all, so it could not verify the p=8 hashes dmart 1.5.x wrote;
/// that is why this binds the reference library and not libsodium.
///
/// LINKING: `DirectPInvoke` + a static archive for the fully static musl binary
/// (see dmart.csproj) — a musl static-pie has no working dlopen, so the symbols
/// must be bound at link time exactly as SQLite's are. Every other build
/// resolves libargon2.so.1 normally, and the packages declare a dependency on
/// it.
/// </remarks>
internal static partial class Argon2Native
{
    // The SONAME, not the linker name: packages ship libargon2.so.1, and only
    // the -devel package provides the libargon2.so symlink. Naming the SONAME
    // means dmart needs the runtime package alone.
    private const string Lib = "libargon2.so.1";

    // ARGON2_OK. The full error enum is negative values; we only distinguish
    // success from failure and report the code.
    private const int Argon2Ok = 0;

    /// <summary>
    /// int argon2id_hash_raw(t_cost, m_cost, parallelism, pwd, pwdlen,
    ///                       salt, saltlen, hash, hashlen)
    /// </summary>
    /// <remarks>
    /// The _raw variant, not _encoded: dmart formats its own PHC string (and
    /// has to, because it must keep producing byte-identical output to what
    /// 1.5.x and dmart Python wrote). Raw also avoids handing the C library a
    /// buffer to format into.
    /// </remarks>
    // CA5392 on both imports below. The attribute's semantics are Windows'
    // LoadLibraryEx search order, which the Unix loader does not implement — but
    // the rule it enforces is real on either: never let a P/Invoke resolve out
    // of the current working directory, where a writable cwd turns "load the
    // crypto library" into "run whatever the caller dropped there".
    // SafeDirectories states that intent; it is inert on the Linux builds dmart
    // ships, and it is method-scoped because the attribute is not valid on a type.
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "argon2id_hash_raw")]
    private static partial int Argon2idHashRaw(
        uint tCost, uint mCost, uint parallelism,
        ReadOnlySpan<byte> pwd, nuint pwdlen,
        ReadOnlySpan<byte> salt, nuint saltlen,
        Span<byte> hash, nuint hashlen);

    /// <summary>const char *argon2_error_message(int error_code, argon2_type type)</summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport(Lib, EntryPoint = "argon2_error_message")]
    private static partial IntPtr Argon2ErrorMessage(int errorCode, int type);

    /// <summary>Computes an Argon2id hash into <paramref name="output"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// The library rejected the parameters. Callers validate m/t/p at startup
    /// (DmartSettingsValidator) and verification takes them from a stored hash
    /// that was valid when written, so reaching this means a hash row was
    /// hand-edited or the library disagrees with our bounds — either way it is
    /// a fault, not a wrong password, and must not be reported as one.
    /// </exception>
    internal static void HashRaw(
        ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt,
        int memoryKb, int iterations, int parallelism, Span<byte> output)
    {
        var rc = Argon2idHashRaw(
            (uint)iterations, (uint)memoryKb, (uint)parallelism,
            password, (nuint)password.Length,
            salt, (nuint)salt.Length,
            output, (nuint)output.Length);

        if (rc == Argon2Ok) return;

        // 2 == Argon2_id, for the message table only.
        var msg = Marshal.PtrToStringUTF8(Argon2ErrorMessage(rc, 2)) ?? "unknown";
        throw new InvalidOperationException(
            $"argon2id_hash_raw failed (rc={rc}: {msg}) for m={memoryKb} t={iterations} p={parallelism}");
    }
}
