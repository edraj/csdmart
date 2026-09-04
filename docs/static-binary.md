# Fully static Linux binary

The normal publish produces a Native AOT binary that still needs a libc, an
OpenSSL, and a `libe_sqlite3.so` at runtime — so it is tied to the distro that
built it. Glibc makes that worse than it sounds: a Fedora build wants
`GLIBC_2.38`, EL9 ships 2.34 and Debian 12 ships 2.36, so the artifact runs on
neither.

A **musl static-pie** build has no such coupling. One binary, zero shared
libraries, runs on any x86-64 Linux.

## Building it

```
podman run --rm \
  -v "$PWD":/src:z -v ~/.nuget/packages:/nuget:z \
  -e NUGET_PACKAGES=/nuget -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0-alpine sh -c '
    apk add --no-cache clang lld build-base cmake zlib-static \
                       openssl-dev openssl-libs-static \
                       sqlite-dev sqlite-static &&
    dotnet publish dmart.csproj -r linux-musl-x64 -c Release \
      -p:PublishAot=true -p:StaticExecutable=true -p:StaticOpenSslLinking=true'
```

Output lands in `bin/Release/net10.0/linux-musl-x64/publish/dmart`, ~52 MB.

Run the container as **root** — do not pass `--userns=keep-id`, which makes
`apk` fail on its log file. Rootless podman already maps container root to your
host user, so the artifacts come out owned by you.

## What each switch does

| Switch | Effect |
|---|---|
| `StaticExecutable=true` | Links `-static-pie`. Also gates the SQLite block in `dmart.csproj`. |
| `StaticOpenSslLinking=true` | Skips the prebuilt crypto shim and builds `System.Security.Cryptography.Native` from source mid-publish, with the dlopen path compiled out, then links `-lssl -lcrypto`. |
| (csproj, automatic) | `DirectPInvoke Include="e_sqlite3"` + `NativeLibrary Include="$(SqliteStaticLib)"`. |

Both native dependencies reach the binary the same way: **bind the symbols at
link time instead of loading a library at runtime.** A musl static-pie has no
working `dlopen`, which is what broke every earlier attempt — the crypto shim
ships as 291 `*_ptr` BSS variables populated by `dlopen`/`dlsym`, and
SQLitePCLRaw resolves `libe_sqlite3.so` the same way.

`SQLitePCLRaw.lib.e_sqlite3` ships a `.a` for `browser-wasm` only — every other
RID gets a `.so` — so the SQLite archive comes from the build image
(`apk add sqlite-static`). Its `sqlite3_*` symbols are the standard ones
SQLitePCLRaw P/Invokes, so no amalgamation build is needed. Point
`SqliteStaticLib` elsewhere to override.

## Verifying

```
file   bin/Release/net10.0/linux-musl-x64/publish/dmart   # → static-pie linked
readelf -d bin/.../dmart | grep -c NEEDED                 # → 0
strings -a bin/.../dmart | grep -c 'libe_sqlite3\|libssl\.so'   # → 0
```

`--version` is not a sufficient smoke test — it never touches either native
dependency. Run `dmart migrate` against **both** backends; that is what
exercises OpenSSL (TLS to PostgreSQL) and SQLite.

## Caveats

- **You own CVE patching.** Statically linked OpenSSL and SQLite do not get
  distro security updates. A libssl or SQLite advisory becomes a dmart
  rebuild-and-reship.
- **Alpine's SQLite is not byte-identical to SQLitePCLRaw's.** It adds
  `ENABLE_DBSTAT_VTAB`, `ENABLE_PERCENTILE` and `ENABLE_UNLOCK_NOTIFY`, and
  drops `ENABLE_SNAPSHOT`. The snapshot APIs are unused, so the drop is inert.
  But `dbstat` becoming available makes
  [`DbSizeInfoPlugin`](../Plugins/BuiltIn/DbSizeInfoPlugin.cs) inaccurate in a
  static build: it hardcodes "the bundled SQLite build does not include dbstat"
  without probing, so per-table sizes stay refused even though they would now
  work.
- **Plugins are unaffected.** This used to be the blocker for making static the
  primary artifact: the missing `dlopen` that forces SQLite and OpenSSL to be
  link-bound also ruled out `NativeLibrary.Load`, so in-process `.so` plugins
  could not load at all. Those were removed — every plugin is now a subprocess
  that dmart talks to over stdin/stdout, which a static binary spawns exactly
  like a dynamic one. See `custom_plugins_sdk/README.md`.
## CI

The `static-build` job in `ci.yml` builds this and asserts it stays standalone:
zero `NEEDED` entries, no `libe_sqlite3`/`libssl.so`/`libcrypto.so` strings,
then `dmart migrate` on SQLite to prove the linked engine actually opens a
database.

Those assertions are the point. A binary that quietly regained a dynamic
dependency would still publish, and would still run **on the CI runner**, whose
distro happens to have the libraries — the breakage only shows up on someone
else's machine.

Like `container-build`, it is scoped rather than run on every PR: it triggers
on changes to any `.csproj`, `Directory.Build.*` or `ci.yml`, and
unconditionally on pushes to master. A new NuGet package that reaches its
native library through `dlopen` arrives via a `PackageReference`, so that
filter catches the case that matters.

`release.yml` does **not** build this artifact — it is not shipped today. If it
ever is, add a version check alongside: statically linked OpenSSL and SQLite
only get patched when this is rebuilt.
