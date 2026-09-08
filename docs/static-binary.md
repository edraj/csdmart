# Fully static Linux binary

The normal publish produces a Native AOT binary that still needs a libc, an
OpenSSL, and a `libe_sqlite3.so` at runtime — so it is tied to the distro that
built it. Glibc makes that worse than it sounds: a Fedora build wants
`GLIBC_2.38`, EL9 ships 2.34 and Debian 12 ships 2.36, so the artifact runs on
neither.

A **musl static-pie** build has no such coupling. One binary, zero shared
libraries, runs on any Linux of its architecture. Both x86-64 and arm64 are
built.

## Shipping it

`release-verifiable.yml` builds this as `dmart-<version>-linux-musl-x64.tar.gz`
and `dmart-<version>-linux-musl-arm64.tar.gz` on every `v*` tag, alongside the
glibc tarballs. It rides the same matrix as those, so it goes through the same pin-to-the-tagged-commit check, the same
per-RID CycloneDX SBOM, and the same keyless signing and SLSA provenance
attestation — a separate job would have meant a second copy of the logic that
guarantees nothing is signed that was not built from the tagged commit.

Two things differ, and both are asserted in the release rather than assumed.
The tarball carries **no `libe_sqlite3.so`**: it is one file, which is the
entire point. And before signing, the job checks `readelf -d` reports zero
`NEEDED` entries and that no `libe_sqlite3` / `libssl.so` / `libcrypto.so`
strings survive, then runs `--version` inside **busybox** rather than the
Alpine image it was built in — proving it starts somewhere that has none of its
build-time surroundings.

`scripts/verify-release.sh` requires both tarballs, so a release that lost
either fails verification rather than passing with one artifact fewer.

The two static legs differ **only in their runner**. The Alpine SDK image and
the busybox image used for the smoke test are both multi-arch manifest lists
carrying `linux/arm64`, so the same pinned digests serve both; and as with
`linux-arm64`, the arm64 leg has to run on a real arm64 machine because
NativeAOT cannot cross-compile.

## Building it locally

`sh ./build.sh --aot --static --rid linux-musl-x64` is the scripted path and
the one CI uses (`--rid linux-musl-arm64` for the other); it refuses a non-musl
RID, since a static-pie is a musl construct. To do it by hand in a container:

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

Output lands in `bin/Release/net10.0/linux-musl-x64/publish/dmart`, ~52 MB
(the arm64 binary is about the same). Building arm64 by hand needs an arm64
host or `--platform linux/arm64` under emulation, which is slow enough that
the CI runner is usually the better path.

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
  rebuild-and-reship. This is why the glibc tarballs still ship alongside it
  rather than being replaced by it: on those, a distro `openssl` update fixes
  the binary in place.
- **Alpine's SQLite is not byte-identical to SQLitePCLRaw's.** It adds
  `ENABLE_DBSTAT_VTAB`, `ENABLE_PERCENTILE` and `ENABLE_UNLOCK_NOTIFY`, and
  drops `ENABLE_SNAPSHOT`. The snapshot APIs are unused, so the drop is inert.
  `dbstat` becoming available is a genuine difference, and a visible one:
  [`DbSizeInfoPlugin`](../Plugins/BuiltIn/DbSizeInfoPlugin.cs) returns real
  per-table sizes on this build and the "unavailable" fallback on a dynamically
  linked one. It asks rather than assumes, so `GET /db_size_info/` answers the
  same question PostgreSQL does when running this artifact.
- **Plugins are unaffected.** This used to be the blocker for making static the
  primary artifact: the missing `dlopen` that forces SQLite and OpenSSL to be
  link-bound also ruled out `NativeLibrary.Load`, so in-process `.so` plugins
  could not load at all. Those were removed — every plugin is now a subprocess
  that dmart talks to over stdin/stdout, which a static binary spawns exactly
  like a dynamic one. See `custom_plugins_sdk/README.md`.
## CI

The `static-build` job in `ci.yml` builds the **x86-64** one and asserts it
stays standalone:
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

It deliberately does not also build arm64. The regression this job exists to
catch — a dependency that resolves a native library at runtime — is not
architecture-specific, so the x86-64 leg catches it, and the job runs on the
self-hosted x86 runner. Genuinely arm-specific breakage (the toolchain, the
static archive path) surfaces in the release workflow, which can be dry-run
dispatched without cutting a tag.

`release-verifiable.yml` ships it — see "Shipping it" above. `release.yml`
still does not build it, and does not need to: the verifiable workflow is the
one that signs and attests, and this artifact has no reason to exist unsigned.

The CVE consequence is now live rather than hypothetical: the linked OpenSSL
and SQLite are only patched when a release is cut. `dist/scan-runtime-cves.sh`
runs against the produced binary in the release job, which covers the .NET
runtime compiled into it — it does not cover the linked OpenSSL, so an advisory
there is on us to notice.
