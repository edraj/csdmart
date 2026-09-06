# Changelog

## Unreleased

### Changed

- **The release build is faster where it was measured to be, and unchanged
  where it was not.** Consolidating the Linux packages onto one binary took
  `release.yml` from 24 minutes wall clock to 11 (the Fedora RPM went 5 min → 1,
  the `.deb` 4 → 1). That moved the bottleneck rather than removing it, so this
  release addresses where the time actually went:

  - **The two glibc legs of `release-verifiable.yml` ran
    `dnf install dotnet-sdk-10.0` on every release**, cold, on a hosted runner.
    They now build inside a published `dmart-el9-builder` image, multi-arch and
    pinned by digest, with the SDK and toolchain baked in.

    **This is not a speed improvement.** Over thirteen runs of the same leg —
    eight before the change, five after — the median is `584 s` on both sides.
    The spread within either group (523–651 s before) is far wider than any
    effect the change could have. Two earlier figures quoted for it, "~3
    minutes" and then "~50 seconds", were both single-sample comparisons drawn
    from that spread and neither survived being measured properly.

    The glibc legs do take ~10 minutes against ~6–7 for the musl legs. That gap
    is real and still unexplained; it is not the SDK install.

    The reason this change stays is the other one: it removes an **unpinned**
    `dnf install` from the path that produces signed artifacts. The SDK a
    release was built with used to be whatever the AlmaLinux mirrors served
    that day; it is now a recorded property of a digest-pinned image, which the
    image also reports at `/etc/dmart-builder-sdk-version`.

  - **A NuGet cache was added here and then removed again.** All four legs
    restore from scratch, which looked like obvious waste — but GitHub scopes
    caches by ref, and this workflow only runs on tag pushes and manual
    dispatch. It never runs on the default branch, so it never writes a cache
    another ref can read, and every tag is a new ref with a fresh scope. On a
    dry run it missed even its `restore-keys` prefix while still spending 3–5 s
    a leg saving an entry nothing would restore. A comment now records why
    there is no cache, so the cold restores are not mistaken for an oversight.

  And one that is worth knowing but is not a build cost at all: 424 s of the
  v1.5.3 release was the signing job waiting for the GitHub Release object to
  be created after the tag was pushed. Creating the release promptly removes
  it; no code is involved.

  Two things deliberately not changed. The Windows (9 min) and macOS (7 min)
  AOT builds are different RIDs with nothing to share. And the two workflows
  still compile the Linux targets separately: `release-verifiable.yml` exists
  to establish that an artifact was built by hosted CI from the tagged commit,
  and feeding it a self-hosted binary would forfeit exactly that.

## v1.5.3 — 2026-09-06

### Added

- **The container image is published for arm64 as well as amd64.** `latest` and
  `<version>` are now multi-arch manifest lists; each architecture also gets an
  explicit `<version>-amd64` / `<version>-arm64` tag. Previously the aarch64
  Alpine package was built on every release and then thrown away, because the
  container job consumed only the x86_64 one.

  The arm64 image is built on `ubuntu-24.04-arm` with docker, matching how the
  aarch64 APK is already built — `apk add` has to execute aarch64 binaries, so
  a native runner beats emulation. The manifest job then asserts the published
  index actually carries both architectures: an index listing one arch, or
  listing an arch whose manifest never pushed, otherwise succeeds silently.

### Changed

- **The Linux packages are built from one binary instead of three.** The Fedora
  RPM, the EL9 RPM and the `.deb` each ran their own `linux-x64` AOT compile —
  three builds of the same target, about 12 minutes of a three-runner pool, and
  three chances for the packages to quietly diverge.

  `build-el9-rpm` now publishes the binary it already compiles, and the other
  two consume it via `DMART_PREBUILT_BIN`. No new job: EL9 was already doing
  this compile in an AlmaLinux 9 container. `dmart.spec` needed no change
  either — its `%build` only compiles when `dmart.csproj` is present, which is
  the SRPM-rebuild path, so it was written for this from the start.

  **EL9 is the right one to build on.** AlmaLinux 9 has the oldest glibc of the
  three, so a binary built there is the one most likely to run everywhere the
  other two need to.

  **What this does NOT change is compatibility.** An earlier version of this
  entry claimed sharing the EL9 binary widens what the `.deb` supports. It does
  not: the v1.5.2 and v1.5.3 debs require an identical set of glibc symbols,
  floor `GLIBC_2.34` in both. A binary's floor is the highest symbol version it
  actually references, not the builder's glibc, and the Debian 12 builder was
  already producing 2.34. Every package supports exactly what it did before.

  The real gains are one binary instead of three, so they cannot diverge, and a
  much shorter build — with the binary supplied, the `.deb` job needs no .NET
  SDK, no clang and no Microsoft apt feed at all, just `dpkg-dev`.

  This is the same build-once-package-many move the container image made when
  it stopped compiling dmart and started installing the Alpine package.

- **dmart serves its UIs from inside the binary, and only from there.** The
  filesystem fallback in `CxbMiddleware`/`CatalogMiddleware` — `{BaseDir}/cxb`,
  `/usr/lib/dmart/cxb`, `/app/cxb` — is gone, along with the second copy of the
  assets the Alpine package laid down at that path.

  Both existed for one reason: `ManifestEmbeddedFileProvider` does not survive
  Native AOT on musl, so the middlewares fell through to disk and the APK
  shipped 5.9 MB of duplicate assets to catch them. That is what kept `/cxb`
  and `/cat` alive in the Alpine package and container while the static tarball
  404'd for two releases. v1.5.2 replaced the reader with one that works on
  glibc and musl alike, which made the duplicate dead weight.

  The consequence is worth stating plainly: **embedded is now the only path, on
  every artifact.** A regression 404s every UI everywhere rather than only on
  musl. That is why the release asserts `/cxb/` and `/cat/` actually serve — on
  all four tarball legs and on both container images — before anything is
  published.

- **The container image is 36% smaller — 92.3 MB to 58.9 MB.** Almost none of
  that was Alpine, whose base rootfs is 8.7 MB. It was waste:

  - **21.8 MB: the `.apk` was carried twice.** It was `COPY`ed to `/tmp` and
    deleted in the *next* `RUN` — but a delete in a later layer reclaims
    nothing, so the package stayed in the image alongside its installed copy.
    A `RUN --mount=type=bind` leaves no layer at all. This single mistake was
    over half the image's overhead.
  - **~7.5 MB: `bash` and `curl`, which nothing used.** `entrypoint.sh` is
    `#!/bin/sh` and shells out to only `tr`, `head` and `chmod`; there is no
    `HEALTHCHECK`, and the release smoke test curls from the host. Five
    requested packages pulled in 38; between them these two dragged `libcurl`,
    `brotli-libs`, `nghttp2`, `libidn2` and `libunistring`.
  - **5.9 MB: the SPAs were shipped twice.** The APK lays `cxb` and `catalog`
    down at `/usr/lib/dmart/` *and* they are embedded in the binary. That path
    is the filesystem fallback the middlewares used when the embedded reader
    failed under Native AOT on musl — it is what kept `/cxb` and `/cat` working
    in this image while the static tarball 404'd. Since v1.5.2 the embedded
    reader works, so the image drops the copy.

  `jq` stays — a join sub-query carrying a `jq_filter` shells out to it.
  `tzdata` stays at 433 KiB. `krb5-libs` stays: the GSSAPI PAL is disabled in
  this image, which *probably* makes it redundant, but that is not a reason to
  drop 1.7 MB from a supported auth path without testing it.

  Removing the SPA fallback means the primary path now has to be checked, so
  the release smoke test asserts `/cxb/` and `/cat/` return 200 before the
  image is pushed. It previously checked only `/health/ready` — which is how
  a container serving neither UI would have passed.

- **The container mocks SMS OTP by default.** It ships no SMS gateway, and
  `SmsSender`'s unconfigured path logs "SMS gateway not configured — dropping
  message" and returns false. At the previous `MOCK_SMPP_API=false` default
  that meant `/user/otp-request` minted a code and silently failed to deliver
  it, so OTP login could not be completed and nothing said why. The generated
  config now sets `MOCK_SMPP_API=true` with `MOCK_OTP_CODE=123456`, and the
  first-run banner says so. Configure `SEND_SMS_OTP_API` + `SMPP_AUTH_KEY` and
  unset it for real delivery. `MOCK_SMTP_API` is deliberately left alone —
  email OTP has the same gap, but that is a separate call.

## v1.5.2 — 2026-09-06

### Fixed

- **The fully static binary returned 404 for `/cxb` and `/cat`.** Both SPAs
  were unreachable on the musl artifact in v1.5.0 and v1.5.1 — the one build
  whose entire selling point is needing nothing beside it. The glibc builds
  were unaffected.

  The assets were embedded correctly the whole time; only the reader failed.
  `CxbMiddleware` and `CatalogMiddleware` loaded them through
  `ManifestEmbeddedFileProvider`, which does not work under Native AOT on musl.
  That throw was caught and fell through to a filesystem fallback looking for a
  `cxb/` directory next to the binary — which the static tarball deliberately
  does not ship, because it is one file. Both strategies failed, and
  `if (fileProvider is null) return app;` returned **silently**, so nothing in
  the logs or the build said anything was missing.

  Both middlewares now read the embedded manifest XML directly, the way
  `Cli/SeedCommand.cs` already did for seed spaces and `LanguageLoader` for
  translations — which is exactly why `languages loaded: 3 … from embedded`
  appeared in the static binary's startup log while the SPAs did not. That
  reader works on glibc and musl alike, so the two builds no longer diverge.
  The filesystem fallback stays for the Docker and RPM layouts.

  A missing bundle now logs a warning naming the URL that will 404. Silence is
  how this shipped twice.

  Verified on a locally built `linux-musl-x64` binary (0 `NEEDED`): both SPA
  roots, `index.html`, all 12 referenced JS/CSS/icon assets, the `<base href>`
  rewrite, the extensionless deep-route fallback, the root `/favicon.ico`
  redirect, and non-default `CXB_URL`/`CAT_URL` prefixes.

### Changed

- **The release now proves each binary actually serves its embedded SPAs.**
  `release-verifiable.yml` starts the freshly published binary against SQLite
  and requires `/cxb/` and `/cat/` to return 200 — inside busybox for the
  static legs. Nothing else could have caught the bug above: `curl.sh` check 49
  accepts a 404 as "SPA not built" and the test suite treats a missing bundle
  as skip, both correctly, since a dev build legitimately has no SPA. The
  release job is the only place that knows the bundle is present, having
  unpacked it two steps earlier.

## v1.5.1 — 2026-09-05

### Added

- **A fully static arm64 binary**, `dmart-<version>-linux-musl-arm64.tar.gz`,
  alongside the x86-64 one v1.5.0 introduced. Same construction: SQLite and
  OpenSSL bound at link time, zero `NEEDED` entries, no `libe_sqlite3.so`
  beside it — one file that runs on any arm64 Linux regardless of distro or
  glibc version.

  It is a new entry in the existing build matrix rather than any new
  machinery, so it goes through the same pin-to-the-tagged-commit check, SBOM,
  signing and SLSA attestation as everything else. It differs from the x86-64
  static leg **only in its runner**: the pinned Alpine SDK and busybox digests
  are both multi-arch manifest lists that already carry `linux/arm64`, and as
  with `linux-arm64`, it has to be a real arm64 machine because NativeAOT
  cannot cross-compile.

  `scripts/verify-release.sh` now requires it, so a release that lost it fails
  verification rather than passing with one artifact fewer. The `static-build`
  job in `ci.yml` still builds only x86-64 — the regression it exists to catch,
  a dependency reaching its native library through `dlopen`, is not
  architecture-specific.

## v1.5.0 — 2026-09-05

### Added

- **The fully static musl binary is now a release artifact.**
  `dmart-<version>-linux-musl-x64.tar.gz` ships on every `v*` tag alongside the
  glibc tarballs, with the same CycloneDX SBOM, keyless signature and SLSA
  provenance attestation as the rest. SQLite and OpenSSL are bound at link
  time, so it has zero `NEEDED` entries and runs on any x86-64 Linux regardless
  of distro or glibc version — one file, with **no `libe_sqlite3.so`** beside
  it.

  It rides the existing build matrix rather than a job of its own, so it goes
  through the same pin-to-the-tagged-commit check and the same signing path by
  construction. A separate job would have meant a second copy of the logic that
  guarantees nothing is signed that was not built from the tagged commit, and
  that is not a thing to keep two copies of. The container image, toolchain
  setup and build flags moved into matrix fields to make that possible.

  Two assertions run before it is signed: `readelf -d` must report zero
  `NEEDED` entries, and no `libe_sqlite3` / `libssl.so` / `libcrypto.so`
  strings may survive. It then reports `--version` from inside **busybox**
  rather than the Alpine image it was built in — proving it starts somewhere
  with none of its build-time surroundings. `scripts/verify-release.sh`
  requires the tarball, so a release that lost it fails verification instead of
  passing with one artifact fewer.

  `build.sh` gained `--static`, so the scripted build path is the one CI uses
  rather than a `dotnet publish` line duplicated into a workflow. It refuses a
  non-musl RID, since a static-pie is a musl construct that glibc cannot link
  into something that actually runs.

  **You own CVE patching for this artifact.** Statically linked OpenSSL and
  SQLite get no distro updates, so an advisory in either becomes a
  rebuild-and-reship. That is why the glibc tarballs still ship alongside it
  rather than being replaced by it.

**Plugins can call back into dmart.** Only in-process `.so` plugins could do
this before, via a C ABI struct — load an entry, run a query, send mail,
broadcast on a channel. Subprocess plugins, the mode the SDK recommended, had
none of it, so "recommended" came with a silent capability cliff and anything
needing a callback had to crash the host when it faulted. That gap is what kept
in-process plugins alive; closing it is what let them be removed.

A plugin may now interleave callback frames into an exchange before its final
response, and dmart answers each on stdin:

```
← {"type":"callback","id":1,"op":"query","args":{"type":"search","space_name":"acme"}}
→ {"type":"callback_result","id":1,"ok":true,"result":{...}}
← {"status":"ok"}
```

Ten ops are available: `load_entry`, `load_user`, `save_entry`, `update_user`,
`send_email`, `ws_broadcast`, `query`, `log`, `get_session_firebase_tokens` and
`get_media_attachment`. The last one base64-encodes the blob, costing about 33%
more bytes on the wire than the file itself; a miss is `{"media":null}` rather
than an empty string, so an absent attachment and a zero-byte one stay
distinguishable.

Support is negotiated: the info frame is now
`{"type":"info","host":{"callbacks":1}}`. A plugin that sees no `host` object
is talking to an older dmart and must not send callbacks — the frame would be
read as its final response. Existing plugins that never send one are
unaffected, and any line that is not a `"type":"callback"` object is still
treated as the response exactly as before.

Two limits bound a misbehaving plugin: 256 callbacks per exchange, and the 30s
timeout now applies per line rather than per exchange — a long honest chain of
callbacks is fine, going silent for 30s is not. A callback that re-enters its
own plugin is rejected rather than allowed to write a second request onto a
pipe that is midway through an exchange.

A `query` runs as the user that triggered the exchange unless it carries an
explicit `as_actor` override, so plugin queries stay inside that user's
permissions by default — the same rule the in-process callback followed.

**Plugins can serve calls in parallel — `"workers": N` in `config.json`.**
Exchanges are serialized per process because the line protocol has no
correlation ids: a reply is matched to its request by arrival order, so two
exchanges sharing one pipe could each read the other's answer. Rather than add
ids and require every plugin to handle concurrent requests itself, dmart now
runs N copies of the executable and dispatches each call to whichever is free.
The plugin contract is unchanged — each worker still sees one message at a
time, so existing plugins work untouched.

Default is 1, i.e. exactly the previous behaviour. It is opt-in because
concurrency changes what a plugin's own state means: a counter, a cache or a
warm connection becomes per-worker rather than per-plugin, and consecutive
calls need not land on the same process. The range is clamped to 1-32 —
`workers` is operator-edited JSON and a stray digit should not decide how many
processes dmart forks.

Genuine parallelism was the one thing in-process `.so` plugins had that
subprocess plugins did not; this closes that gap without putting third-party
code back inside the host process.

### Changed

- **The eight shipped after-hook plugins now run fire-and-forget.** They ship
  `"concurrent": true`, so `audit`, `local_notification`,
  `admin_notification_sender`, `system_notification_sender`,
  `realtime_updates_notifier`, `mcp_sse_bridge`, `semantic_indexer` and
  `resource_folders_creation` no longer add their work to the latency of the
  action that triggered them.

  This is a real behaviour change, not a restoration. The documented default
  has always been `true`, but a source-generated-deserializer defect fixed in
  the previous release meant the field never survived JSON, so every after-hook
  had in fact always been awaited; that release pinned `false` to hold
  behaviour still while the mechanism was fixed. This is the deliberate flip
  that pin was there to make reviewable.

  For the notifiers, the audit log and the indexer, not blocking the response
  is the entire point. **`resource_folders_creation` is the one to know about:**
  it materializes `/schema` on Space create and `people/{shortname}` plus five
  sub-folders on User create, so a create response can now return before those
  folders exist. A client that creates a Space and immediately uploads a schema
  can lose that race. It does not fail — a missing parent folder is an explicit
  *allow* for both folder-level gates — but the write lands without
  folder-level validation, which in the shipped configuration is a no-op only
  because the auto-created folder declares no restrictions. `curl.sh` check 49
  already polls for the folder rather than assuming it. Set `"concurrent":
  false` in that one plugin's `config.json` to keep it awaited.

**Every plugin process is told what the host supports, including after a
crash.** The `{"type":"info","host":{…}}` frame was sent once, by the loader,
to the process running at startup. A plugin that cached the answer — as the
SDK sample does — silently stopped making callbacks after its first crash,
because the replacement process had never been told, and nothing in the log
said so. The frame is now replayed by whichever code starts a process, so a
respawned worker and a brand-new one are indistinguishable.

**A plugin that dies mid-exchange is no longer retried once it has made a
callback.** The retry exists for a plugin that dies before doing anything;
after a callback has been serviced a `save_entry` may already have landed, and
replaying the request would double it.

**API plugins can return binary responses.** The
`{"binary":true,"content_type":…,"body_b64":…,"filename":…}` envelope was only
honoured on the in-process path; it now works for every plugin. Without this,
removing in-process plugins would have silently taken binary responses with it.

### Fixed

- **The last blocker to running after-hooks concurrently was in the test
  harness, not the plugins.** `TestUserCleanup` deletes the rows a hook wrote
  before deleting the user that owns them — `resource_folders_creation`
  materializes `personal/people/{shortname}/*` entries whose
  `owner_shortname` points at the new user. Pinned `"concurrent": false` that
  hook finishes inside the request, so its rows are always there to purge. Set
  `"concurrent": true` it is dispatched with `Task.Run`, so its inserts could
  land *after* the purge, and the user delete then tripped the very foreign key
  the helper exists to avoid.

  It surfaced as `FOREIGN KEY constraint failed` attributed to whichever test
  happened to be running, which is why it looked like an unrelated flake that
  moved between test classes between runs — three different classes across the
  runs that caught it. The between-test drain added previously could not cover
  it: that settles hooks *after* the test method, and this cleanup runs inside
  it. The helper now settles in-flight hooks before purging, in one place that
  all eight affected test files already route through.

  Measured: unpinned went from 1 extra failure in 3 runs to **0 in 6**; pinned
  is unchanged at 0. Behaviour is unchanged — the eight plugins stay pinned —
  but the flip is no longer gated on an unexplained flake.

- **The frontend SBOM listed 300+ things that do not ship.** It asked
  `yarn list --production` — what `package.json` calls a runtime dependency —
  and cxb declares `@tailwindcss/vite`, `tailwindcss`, `vite-plugin-static-copy`,
  `vite-plugin-svelte-md` and `mdsvex` as dependencies. All are build-time, and
  between them they dragged in esbuild, lightningcss, `@tailwindcss/oxide`,
  `@parcel/watcher` and some seventy per-platform native binaries for operating
  systems the artifact does not run on. The document asserted every one of them
  ships inside `/usr/bin/dmart`.

  That is what produced the SBOM-driven false positives. `jmespath` (a
  `svelte-jsoneditor` dependency that tree-shakes away entirely —
  `cxb/vite.config.ts` already lists it under `skipChunks`) drew two critical
  JMESPath CVEs that turned out to be against the Ruby and PHP implementations.
  `apexcharts`, pulled in by `flowbite-svelte`, is dual-licensed and free only
  under $2M annual revenue — a real licence question, raised about a package
  none of whose JavaScript reaches the bundle.

  The inventory now comes from the built bundle. The apps are built with
  sourcemaps and every `node_modules/<pkg>` path in them is collected, so
  tree-shaken code is absent by construction rather than by a maintained
  exclusion list. Stylesheet references (`@import "tailwindcss"`,
  `@plugin 'flowbite/plugin'`, `@source ".../node_modules/<pkg>"`) are unioned
  in: that code does not ship but its generated output does and carries its
  licence, and Vite emits no CSS sourcemaps, so leaving it out would
  under-report. `flowbite` is the live example — its plugin is where those
  `apexcharts` CSS classnames in the bundle actually come from.

  **434 components became 75**, and the count is not the interesting part.
  The old set both over-reported build tooling *and* missed **`svelte` itself**
  — the framework runtime, unquestionably in the shipped bundle, absent from
  the inventory because it is declared a devDependency. Every one of the 75 now
  resolves a licence locally; the npm-registry fallback added alongside the
  licence fix is no longer reached, because the packages that needed it were
  the per-platform binaries that never shipped.

  Generation now fails if a build fails or emits no sourcemaps, rather than
  quietly producing a thinner document, and it reports how many lockfile
  entries were excluded so a shrunken inventory is never mistaken for a broken
  one. The SBOM job now builds the frontends (~40s); `release.yml` builds them
  in a separate parallel job whose output it cannot see.

- **Declared defaults across the wire model were silently discarded.** #234
  fixed this for `PluginWrapper`; the same defect ran through most of the
  model. On meeting an init-only property, the source-generated deserializer
  abandons the parameterless constructor for
  `ObjectWithParameterizedConstructorCreator`, which assigns every such
  property from an args array and passes `default(T)` for whatever the payload
  omitted. The initialisers ran and were immediately overwritten.

  It only showed where the declared default differs from `default(T)`, which is
  what kept it hidden — `Response.Status = Status.Success` looked correct
  because `Success` is the enum's zero member. The same coincidence in reverse
  is where it did real damage:

  - `Space`, `Role`, `Group` and `Permission` deserialized with
    `resource_type` **`user`**, because `ResourceType.User` is the zero member.
  - A user parsed without an explicit language came back **Arabic**, not
    English — `Language.Ar` is the zero member and `= Language.En` was dropped.
  - Every `= new()` collection arrived as `null` and every `= ""` string as
    `null`, which is what forced the `?? ""` coercions in
    `SpaceRepository.UpsertAsync`.
  - `Query.Limit` arrived as `0` rather than `10`, and
    `Query.FilterSchemaNames` as `null` rather than `["meta"]`. `Query` is
    deserialized straight from request bodies by `CsvHandler`,
    `ImportExportHandler`, `ExecuteTaskHandler` and `AlterationHandler`.

  The 67 properties that carry a default are now `set` rather than `init`,
  which puts the generator back on the real constructor. Only those properties
  changed: the rest stay init-only, and types never deserialized from JSON
  (`DmartRole`, `DmartPermission`) were left alone. Types with `required`
  members keep the parameterized creator, but it then carries only the required
  members — which a payload must supply anyway — so the rest keep their
  declared defaults.

  `SpaceRepository`'s `?? ""` backstops stay. They no longer cover an omitted
  field, but a payload that spells out `"icon": null` still lands a null in a
  non-nullable string, because System.Text.Json does not enforce nullability at
  runtime. `SeedSpaceMetaTests` previously asserted the broken shape and is now
  inverted to pin the corrected one.

- **The frontend half of the SBOM now carries licences.** `yarn.lock` records
  only name, version and integrity — it has no licence field — so syft reading
  it emitted 434 components with no licence at all, and every per-RID document
  inherited that on merge. A reviewer could not tell an MIT dependency from a
  revenue-gated commercial one, and Dependency-Track reported the whole tree as
  unlicensed rather than flagging the single term that needs a decision.
  `dist/frontend-sbom.sh` now resolves licences from the installed
  `node_modules` tree, falling back to the npm registry for the optional
  per-platform native binaries (`@esbuild/win32-*`, `lightningcss-*-msvc`,
  `@tailwindcss/oxide-*`, `fsevents`) that never install on the build machine
  and so can never be resolved locally. Coverage went 0/434 → 434/434; the
  component set is unchanged.

  Licences are encoded the way CycloneDX requires rather than pasted into one
  field: SPDX identifiers as `license.id`, compound terms as an SPDX
  `expression`, and anything unrecognised as `license.name`. `license.id` is a
  schema enumeration, and an out-of-enum value fails the validation
  `actions/attest-sbom` runs before signing — so an unknown string degrades to
  a plain name rather than breaking a release. Non-SPDX licences are printed at
  generation time as needing review, which is how `apexcharts` (dual-licensed,
  free only under $2M annual revenue) becomes visible instead of silently
  reading as unlicensed.

  Generation fails if fewer than half the components resolve a licence. The two
  sources fail independently, so losing either still clears the floor; losing
  both is the case worth refusing, because an SBOM asserting 434 unlicensed
  dependencies reads as a licence finding rather than the tooling failure it
  is. `FRONTEND_SBOM_OFFLINE=1` skips the registry lookup for airgapped builds.

- **A plugin's `config.json` defaults were silently discarded.** `PluginWrapper`
  declares `ordinal` defaulting to 9999, `concurrent` to `true` and
  `dependencies` to an empty list, and the SDK documents all three. None of them
  survived deserialization: a config that omitted a field got `0`, `false` and
  `null` instead.

  The cause is a sharp edge in the source-generated deserializer. With any
  init-only property, it abandons the parameterless constructor for
  `ObjectWithParameterizedConstructorCreator`, which assigns *every* such
  property from an args array and passes `default(T)` for whatever the JSON left
  out. The constructor still ran, so the initialisers executed and were then
  immediately overwritten. `new PluginWrapper()` was correct throughout, which is
  why this survived: any test that built the object in C# saw the documented
  values, and only a test that went through JSON could have caught it. The
  properties are now `set` rather than `init`, which puts the generator back on
  the real constructor.

  The practical effect was on `concurrent`, which `PluginManager` reads directly
  to choose between fire-and-forget and awaited after-hook dispatch. Every
  shipped plugin omitted the field, so every after-hook had been awaited —
  the opposite of the documented default, and of what the dispatch code was
  written for.

  **Runtime behaviour is deliberately unchanged by this release.** The eight
  shipped after-hook plugins now state `"concurrent": false` explicitly, so they
  keep running awaited exactly as before. Fixing the mechanism and changing when
  every hook in the system runs are two different changes, and only the first
  belongs in a bug fix. Moving them to fire-and-forget is now a one-line,
  reviewable decision per plugin.

  New plugins are unaffected by the pinning and get the documented default:
  omitting `concurrent` means fire-and-forget, as the SDK has always said.

- **The .NET half of the SBOM now carries licences, and one of them needs a
  decision.** Five components had none, and an unlicensed component is
  indistinguishable from a permissively licensed one — so a term that needs
  attention read as uninteresting.

  `Json.More.Net`, `JsonPointer.Net` and `JsonSchema.Net` declare
  `<license type="file">OSMFEULA.txt</license>`, an **Open Source Maintenance
  Fee** agreement: the source is MIT, but the pre-compiled Binary Release — the
  NuGet package we consume — carries a monthly fee for users in
  revenue-generating activities with annual gross revenue at or above
  **US$10,000**. `JsonSchema.Net` is a direct dependency behind
  `SchemaValidator` and `PreflightService`, so it is compiled into every
  artifact we ship. The two runtime packs were simply missed: they declare MIT,
  but `dist/sbom.sh` injects them outside the restore graph the CycloneDX tool
  reads, so nothing ever asked.

  Licences are now read from each package's `.nuspec` for anything the tool
  left blank, and packages shipping their own licence text are printed at
  generation time rather than reduced to a count. 34/34 components carry a
  licence, and the document still validates against CycloneDX 1.6.

- **`GET /db_size_info/` returns per-table sizes on builds that can provide
  them.** `DbSizeInfoPlugin` hardcoded that `dbstat` is unavailable. That was
  true of the SQLitePCLRaw `e_sqlite3` build and is not true of the static musl
  artifact, which links Alpine's SQLite and does compile
  `SQLITE_ENABLE_DBSTAT_VTAB` in — so the one artifact that could answer
  refused to, and the refusal named a build it was not running. It now runs the
  query and falls back only when that fails. The test asserted the failure
  unconditionally, which is how the stale assumption survived; it accepts both
  outcomes and pins what each must contain.

- **The test suite settles fire-and-forget plugin hooks between tests.**
  `TestParallelization.cs` runs the assembly serially because the suite shares
  one database and process-global plugin state, but that serializes *tests*,
  not their side effects: a concurrent after-hook is dispatched with `Task.Run`
  and outlives the request, so one test's hooks could still be writing while
  the next ran. An assembly-level `BeforeAfterTestAttribute` now waits for them.

  `InFlightTracker` gained `WaitForIdleAsync` for this. `DrainAsync` could not
  be reused: it cancels `ShutdownToken` as its last act, which is correct once
  at teardown and wrong repeatedly — every hook dispatched afterwards would
  receive an already-cancelled token and unwind immediately, so the hooks would
  silently stop running.

  This is a prerequisite for moving any plugin to `"concurrent": true`, not a
  green light. With the eight shipped plugins temporarily unpinned, the suite
  still produced an intermittent extra failure (1 run in 3, in a different test
  than before the change), so something beyond hook overlap remains. With them
  pinned as they ship, the suite reproduces its baseline exactly across three
  runs, so this costs nothing today.

  `DmartFactory.SettlePluginHooksAsync()` covers the case the between-test hook
  cannot: a test asserting on state a hook affects, where the assertion happens
  before the attribute runs.

- **A user-supplied JSON Schema could kill the process, and another could
  silently switch validation off.** Both are reachable by anyone able to store
  a `schema` entry.

  `{"$id":"https://x/s","allOf":[{"$ref":"https://x/s"}]}` compiles fine and
  recurses only when something is evaluated against it. On the shipped
  `JsonSchema.Net` 9.1.4 that recursed until the stack gave out — and a
  `StackOverflowException` cannot be caught, so the process died and took every
  in-flight request with it. Reaching it needed nothing exotic: store that
  schema, then write one entry whose payload references it. Upgrading to 9.4.0
  turns it into a catchable exception.

  Separately, `JsonSchema.FromText` registers a schema's `$id` into a
  **process-global** registry that refuses to overwrite. dmart recompiles the
  same document routinely, because `ClearCache()` runs on every schema entry
  write — so the second compile threw, `GetCompiledAsync` caught it and
  returned null, and null reads as "schema not found — pass through". Writing
  any schema entry therefore stopped every `$id`-bearing schema from being
  enforced, leaving one warning log as the only trace. dmart's own seed schemas
  declare no `$id`, which is why nothing caught it. Every compile now gets its
  own registry.

  Schema documents are also checked on the way in rather than waved through, so
  an unusable one is refused with the error attributed to its author instead of
  to whoever later writes the first entry against it. The check evaluates a
  trivial instance rather than only compiling, because compiling is exactly what
  fails to notice a reference cycle. Only exceptions reject: a schema that
  legitimately fails against the empty instance — anything with `required` — is
  fine, and so is one that recurses legally through `$defs`.

  The upgrade keeps `JsonSchema.Net` on its Open Source Maintenance Fee terms
  (see the SBOM entry above); the last MIT release, 8.0.5, still has the crash.

### Removed

**In-process `.so` plugins.** dmart no longer loads shared libraries into its
own process via `NativeLibrary.Load`. The C ABI (`get_info`, `hook`,
`handle_request`, `free_string`, `init`, `dmart_plugin_version`), the
`DmartCallbacks` struct and its capability marker, and the C# SDK header in
`custom_plugins_sdk/shared/` are all gone, along with the two `.so` sample
projects — replaced by Python samples that use the protocol above.

This mode ran third-party code inside the host process, so a segfault in a
plugin took dmart down with it, and it could not work in a static build at all
(`dlopen` is unavailable there). The subprocess protocol now covers everything
it could do, including every callback.

**If you have a `.so` deployed**, dmart reports it at startup and on
`GET /info/plugins` as a load failure naming the removal rather than skipping
the directory in silence — a plugin that stops running should never be
something you have to infer from behaviour that quietly stopped happening. Port
it using `custom_plugins_sdk/README.md`; the event and request envelopes are
unchanged, so the handler logic usually transfers as-is.

Two host-side details went with it: `ProcessEnv` (a libc `setenv`
write-through that existed only because in-process plugins read the real
`environ` — child processes inherit the managed view, so the managed API is
enough now), and the `[ThreadStatic]` actor context's dependency on
synchronous native frames.

- **`validate_schema` is gone from the query body.** It had no consumers
  anywhere in `Services`, `Api` or `DataAdapters`, while `docs/query.md`
  advertised it — so it read as a working switch. Removing it changes no
  output: `Query` is deserialized from request bodies and never serialized into
  a response, so a client that keeps sending it is unaffected, since
  System.Text.Json skips unmapped members. (`cxb` also passes `validate_schema`
  to `/managed/entry`, whose handler never declared it either; those calls were
  already inert and are left alone.)

## v1.4.1 — 2026-09-04

### Fixed

**`POST /user/profile` accepts an unchanged `email`/`msisdn` again.** v1.4.0
refused all six contact keys on presence alone. But `email` and `msisdn` are
part of the profile *representation*: a client that reads its profile, edits a
display name and posts the Record back sends them straight back unchanged, and
that ordinary round-trip started failing with `INVALID_DATA` having changed
nothing. They are now refused only when they name a *different* address than
the row holds; an echo, or a null, is the no-op it always was. `new_email`,
`new_msisdn`, `email_otp` and `msisdn_otp` are still refused by name. (#228)

**`POST /user/verify-contact` no longer returns 500 when `code` is missing.**
The field is non-nullable in the request record but nothing enforced that on
the wire, so an omitted `code` reached the hasher as null — and did so
precisely when a live code existed for the destination, i.e. right after
`/otp-request`. Now a `MISSING_DATA` 400, refused before the store is touched
so it cannot spend a verification attempt either. (#228)

**Contact changes reach the audit history again.** `/user/verify-contact`
wrote directly to the user row, skipping the diff the `/user/profile` path it
replaced used to append — so a changed email was invisible to
`/managed/query?type=history`. (#228)

**Confirming a contact no longer rewrites its stored spelling.** An
admin-provisioned or OAuth-sourced `Alice@Example.com` was silently lowercased
the first time its owner confirmed it. The address is now written only on an
actual change. (#228)

**`GET /user/profile` returns the caller's avatar.** Attachments on the user's
own row now come back under `attachments`, in the same shape `/managed/entry`
returns, so a client that already renders `record.attachments` needs no second
call. Avatar only, matching Python's `filter_shortnames=["avatar"]`. (#227)

**`/managed/entry` honours `retrieve_attachments` for spaces, users, roles and
permissions.** Those four returned a bare row and silently ignored the flag.
(#227)

### Security

**`fast-uri` pinned past four HIGH CVEs** (CVE-2026-75899, CVE-2026-75931,
CVE-2026-75975, CVE-2026-76172 — SSRF, host confusion via IDN, IPv6
normalisation, URI parsing). It reached the frontend bundle transitively via
`svelte-jsoneditor` → `ajv`. The declared range already permitted the fix; the
lockfile was stale. Pinned through the root `resolutions` block, as with
`vite` and `esbuild`. (#229)


## v1.4.0 — 2026-09-02

### Breaking — the OTP issuing endpoints are now one endpoint

Two routes are gone. Any client that calls them gets a 422:

| removed | use instead |
| --- | --- |
| `POST /user/otp-request-login` | `POST /user/otp-request` with `"purpose": "login"` |
| `POST /user/password-reset-request` | `POST /user/otp-request` with `"purpose": "reset"` |

`POST /user/otp-request` is now the single issuing API and **requires** an
explicit `purpose`: `login`, `reset`, `register` or `verify-contact`. A request
without one is refused with `INVALID_DATA "invalid purpose"`. Codes never cross
purposes — one minted for `login` cannot complete a signup, and `/user/create`
accepts only a code minted at `register`.

**`POST /user/otp-confirm` is replaced by `POST /user/verify-contact`**, which
owns every contact-plus-OTP operation:

```http
POST /user/otp-request      {"purpose": "verify-contact", "email": "me@x.com"}
POST /user/verify-contact   {"code": "123456", "email": "me@x.com"}
```

Authenticated. Prove control of an address and it becomes yours, verified —
the *same* call whether it is the address already on your row or a new one.
Which of the two it is comes from state the server already holds, so the caller
does not declare intent. A new address is uniqueness-checked before the code is
spent, and verified flags never regress.

Renamed rather than kept: `otp-confirm` was named for the token it consumes,
and *every* OTP redemption confirms an OTP — logging in, registering and
resetting all do — so the name described the whole category while the handler
served one member of it. Every other endpoint here is named for its outcome
(`/user/login`, `/user/create`, `/user/password-reset-confirm`); this one now
is too.

**`POST /user/profile` no longer accepts contact fields.** `email`,
`new_email`, `email_otp`, `msisdn`, `new_msisdn` and `msisdn_otp` are
**refused by name**, with an error pointing at `/user/verify-contact` — not
ignored, because a client still sending `new_email` would otherwise get a 200
and no change. Everything else on the endpoint is unaffected.

The `new_` prefix is gone with them. It was load-bearing only on
`/user/profile`: `email` is part of the profile representation, so a caller
reading their profile, editing a display name and posting it back sends `email`
unchanged — and had that meant "change my email", an ordinary round-trip would
have demanded an OTP for a field nobody touched. A dedicated endpoint has no
representation to echo, so one unprefixed field is unambiguous.

**`ALLOW_PASSWORD_RESET_RESEND_AFTER` is retired.** `ALLOW_OTP_RESEND_AFTER` now
covers every purpose. A `config.env` still carrying the old key **boots with a
warning** rather than failing — a key that was documented last release is not a
typo, and refusing to start would turn this upgrade into an unannounced outage.

**The in-repo frontends (catalog, cxb) were updated in this release.** Any other
client calling the removed routes needs the same treatment.

### Added

- **Abuse controls on issuing.** A resend cooldown (`ALLOW_OTP_RESEND_AFTER`)
  and a daily cap (`MAX_OTP_REQUESTS_PER_DAY`, default 10), both per
  destination. Each is split into two independent budgets — account recovery in
  one, everything else in the other — so no single flood can close both sign-in
  and password reset. Switching purpose *within* a budget is not a bypass.
- **A verify-attempt cap.** `MAX_OTP_VERIFY_ATTEMPTS` wrong guesses per code,
  after which the code is dead. Verification is single-use: a correct code is
  consumed on success and cannot be replayed.
- **Optional implicit registration.** With `ENABLE_OTP_IMPLICIT_REGISTRATION`,
  a login-purpose request for an unknown msisdn or email creates the account on
  redemption. Off by default.
- **OTP history retention.** Consumed and expired rows are swept hourly by
  `OtpHistorySweeper`, keeping `OTP_HISTORY_RETENTION_DAYS` (default 2).

### Security

- **Every OTP verify path is capped and consuming.** Previously some paths
  verified without consuming, so a code could be replayed, and without an
  attempt cap a 6-digit code could be brute-forced.
- **An anonymous caller could deny a victim both sign-in and account recovery.**
  `register` needs no token and no existing user, so one request a minute
  against a known destination held the resend cooldown permanently open and
  swallowed every login and reset as a silent 200. Both the cooldown and the
  daily cap are now bucketed so one flood cannot close the other. A targeted
  flood can still exhaust one bucket; closing that needs a per-caller identity
  this endpoint does not have.
- **Issuing honours the lockout cool-down.** It gated on the raw active flag,
  which lockout clears only via `IsLockedAsync` — so a locked account stayed
  silently un-OTP-able forever after its cool-down expired. For a password-less
  account, whose only credential is the OTP, nothing would ever have unlocked it.
- **Destinations are no longer logged in clear.** Phone numbers and email
  addresses were written at Information on every silent no-op branch; they are
  now an 8-character fingerprint.

### Fixed

- **`DmartClient.ConfirmOtpAsync` sent the wrong body key** and could therefore
  never succeed: it posted `otp` where the request record binds `code`, so the
  server saw no code at all. Latent since the method was added; found while
  reworking the endpoint it calls. It is now `VerifyContactAsync`, with the
  correct key.

- **Password reset was unrecoverable for a mixed-case stored email, and locked
  the account.** Issuing stored the code under the lowercased address while
  confirming looked it up under the raw stored value, so every *correct* code
  returned `OTP_INVALID` — and each attempt counted toward the failed-attempt
  lockout. Mixed-case rows are ordinary: admin provisioning and OAuth both store
  the address as given.
- **A user could not confirm the contact already on their row** if it carried
  any uppercase — `/user/profile` compared a normalised input against the raw
  column ordinally, so the check could never pass.
- **`DROP TABLE IF EXISTS otp;` ran on every startup.** It sat in the idempotent
  create script rather than a migration, and `otp` is the table python-dmart
  uses — so on a shared database every C# restart destroyed it. The C# store is
  the new `otps` table; the legacy one is left alone.
- **A code was consumed before checks that could still fail**, in implicit
  registration, `/user/create` schema validation, and both contact-change paths.
  Each of those failures is recoverable, and each burned a valid code — after
  which a retry inside the resend cooldown answered a silent 200 with nothing
  sent.
- **`IssueAsync` was not atomic.** Supersede and insert ran as two statements,
  so concurrent issues could leave two redeemable codes despite the documented
  invariant.

### Migration

None required. Live OTPs are invalidated by the store change — anyone
mid-flow requests a new code.

## v1.3.3 — 2026-09-02

### Breaking

- **netstandard2.1 consumers: dictionary keys stop being snake_cased on the
  way out.** See the `DictionaryKeyPolicy` entry below for what changed. What
  it means for you depends on whether the payload has a schema, and the two
  cases are very different:

  **With a `schema_shortname` — you get a loud, precise error.** The server
  stores keys verbatim and the space's JSON Schema is the enforcement point, so
  a body written as `endPoint` against a schema declaring `end_point` is
  rejected on write:

  ```
  430 payload failed schema validation:
      required: Required properties ["end_point"] are not present;
      /endPoint: All values fail against the false schema
  ```

  Nothing is silently stored wrong. The fix is to spell the key the way the
  schema declares it — which for dmart's own schemas is snake_case.

  **Without a schema, and for attribute bags — keys round-trip verbatim.**
  `{"myKey": "v"}` is stored and read back as `myKey`. So a netstandard2.1
  caller who has been writing `Attributes["myKey"]` against v1.3.x has that
  data on the server under `my_key`, and after this upgrade the same code
  writes `myKey`. Here there is no schema to catch it: migrate the stored
  entries (read under the old key, write under the new), or spell the key the
  way you want it stored.

  net8.0+ consumers are unaffected — that leg was fixed in v1.3.2 and this
  brings the other into line.

  **On the convention.** snake_case remains the convention for dmart payload
  fields, and the shipped schemas follow it (`end_point`, `request_body`,
  `schema_shortname`). It is a convention that schemas *declare* and validation
  *enforces* per space — it was never a wire-level rewrite, and the server has
  never performed one. The client used to, which meant it silently rewrote keys
  the caller had chosen deliberately and could not turn off. Removing it makes
  the client agree with the server; the schema keeps enforcing the convention,
  and now says so out loud when a key does not match.

### Fixed

- **The two serializer legs disagreed about dictionary keys.** v1.3.2 dropped
  `DictionaryKeyPolicy` from `DmartClientJsonContext` because a dictionary here
  is DATA — attribute bags and nested `Dictionary<string,string>` values, whose
  keys belong to the caller and the space's schema — so snake-casing them is
  silent corruption. `DmartClient.DefaultJsonOptions` kept the policy, and that
  object is both the netstandard2.1 leg's serializer and public API a caller can
  serialize a `Record` with. So the same client contradicted itself by target:
  `"myKey"` shipped verbatim from net8.0+ and as `"my_key"` from netstandard2.1,
  the second of which the server — which sets no `DictionaryKeyPolicy` either —
  then stored under a name the caller could not read back.

  The policy is gone from `DefaultJsonOptions` too. Property names still
  snake_case: those are a fixed shape both ends agree on. Every key the client
  itself writes was already snake_case, so nothing the SDK sends changes. A
  caller who was relying on the netstandard leg to rewrite *their* keys was
  relying on the corruption, and should snake_case them at the source.

### Added

- **`Dmart.Client` can read the decimal-point spelling of an integer.** dmart
  validates payload bodies with JSON Schema, where `"type": "integer"` means "a
  number with a zero fractional part" — `10240.0` satisfies it, so dmart accepts,
  stores and returns that value for a field its own schema calls an integer.
  System.Text.Json refuses to read it into an `int`, which left the client
  **stricter than the server it talks to**: a caller whose model matched the
  schema still got

  ```
  JsonException: The JSON value could not be converted to System.Int32.
  ```

  `IntegralInt32Converter` and `IntegralInt64Converter` (namespace
  `Dmart.Client.Json`) close exactly that gap and nothing wider — a value with a
  real fraction is still rejected, because the schema would reject it too, and
  the comparison runs through `decimal` so integers past 2<sup>53</sup> keep
  every digit. Use them per property via `[JsonConverter(...)]`, which needs no
  options plumbing and stays trim/AOT-safe, or register them on your own
  `JsonSerializerOptions` for a whole body; System.Text.Json wraps them for the
  `int?`/`long?` forms automatically. `DmartClient.DefaultJsonOptions` and the
  source-gen context both carry them, so the client's own types read the same way
  on every target framework.

  Writing is unchanged, and so is what the client hands you: payload bodies still
  arrive byte-exact as `JsonElement`, `10240.0` included. The tolerance is in the
  read, not a rewrite of the data. A field reading back as `10240.0` was *stored*
  that way — Python renders every float like that, as does .NET for a `decimal`
  carrying scale.

  **This supersedes the consumer guidance published in v1.3.1**, which told
  callers that such a field "gets a hard `JsonException: The JSON value could
  not be converted to System.Int32`" and to "map it to `decimal`/`double` … or
  normalise it at the producer". That is right for a `"type": "number"` schema
  and wrong when the schema says `integer` — there the caller's `int` was
  correct and the reader was not. With these converters registered, `int` and
  `long` read the value directly and the workaround is no longer needed.

  The server-side guarantee from the same release is untouched and still
  holds: dmart does not reformat numbers anywhere in the stack, so a field
  that reads back as `1000.0` was stored that way.

## v1.3.2 — 2026-09-01

### Performance

- **`min`/`max` over a JSON field walked the same jsonb path four times per
  row.** Ordering by the value's real type (v1.3.1) means consulting both forms
  of the path: a `jsonb_typeof` guard and a value, in each of two aggregates.
  Written inline that is four descents through `payload::jsonb->'body'->…` for
  every row in the group, where one would do.

  The extraction is now hoisted into a `CROSS JOIN LATERAL` over a FROM-less
  `SELECT`, which computes it once per row and hands it to the aggregates by
  name. Two reducers over the same path share one lateral, so a `min` and a
  `max` on the same field walk it once between them.

  Row-preserving by construction — a `SELECT` with no `FROM` yields exactly one
  row, NULL included — so nothing about which rows the aggregate sees changes.
  Every other reducer mentions its field once and is left alone rather than
  paying for a join that saves nothing.

  **SQLite emits none of this and needs none of it**: its `->>` carries the
  value's own type, so `min`/`max` there are a single `MIN(field)` already.
  `ISqlDialect` gained a `Reducer` overload returning an optional FROM
  fragment alongside the expression; it has a default implementation that
  hoists nothing, so third-party dialects are unaffected.

## v1.3.1 — 2026-09-01

### Security

- **A query could return entries from subpaths the caller has no permission
  on.** The hierarchical subpath filter was built as
  `subpath = $n OR subpath LIKE $n || '/%'` with the caller's subpath bound raw
  and no `ESCAPE` clause. LIKE reads `_` as "any one character", so a query
  scoped to `space/my_folder` also matched `space/myXfolder`, `space/my-folder`
  and every other one-character sibling — and underscores in a folder name are
  the house style, not an edge case.

  It was invisible until v1.3.0 because the ACL predicate cleaned up after it:
  an actor's policy IS escaped on its way to a LIKE pattern, so the
  over-matched rows carried a `query_policies` token the actor's pattern did
  not match and were dropped before anyone saw them. v1.3.0 added a tautology
  skip that omits the ACL predicate when the actor's policies provably cover
  the requested scope — correct in itself, but it removed the masking, and the
  sibling rows started coming back to actors holding no permission on them.

  Every site that builds a subpath prefix now escapes it (`\`, `%`, `_`) and
  matches under `ESCAPE '\'`, via a new `SubpathScope` helper in
  `Dmart.QueryGrammar` — twenty in all, not just the two on the query path:

  | Component | Sites |
  | --- | --- |
  | `EntryRepository` | 10 — list, export, cascade-delete, move, count |
  | `AttachmentRepository` | 4 |
  | `HistoryRepository` | 4 |
  | `QueryHelper` + `Dmart.SqlAdapter` | 2 — the read paths above |
  | `SemanticSearchService` | 1 |

  **Not all of them are reads, and that matters more, not less.**
  `EntryRepository`'s folder cascade uses the predicate to DELETE and to MOVE
  subtrees, and `AttachmentRepository.DeleteUnderSubpathAsync` to delete
  attachments. An over-matching prefix there does not leak a row — it destroys
  or relocates one belonging to a sibling folder. Those paths were never
  masked by the ACL predicate, so unlike the read leak they were reachable
  before v1.3.0 as well.

  The escaping is emitted in SQL rather than applied to the bound value, so one
  parameter still serves both halves of `subpath = $n OR <descendants>` and
  every positional parameter after it stays where it was. The three sites that
  inline the subpath as a SQL literal instead of binding it use a C#
  counterpart with the same substitutions in the same order.
  `SemanticSearchService` keeps its own bare prefix semantics (no `/`
  separator); only its metacharacters are neutralised.

  **Operators:** on v1.3.0, reads against a subpath with a sibling differing by
  a single character at an underscore position should be treated as having been
  unrestricted. Separately, on **any** version, a folder delete or move scoped
  to such a subpath could have reached the sibling's rows. Subpaths without `_`
  or `%` in their names were never affected by either.

### Fixed

- **`Dmart.Client` could not put a `decimal` (or most other CLR scalars) in an
  attribute bag.** `Record.Attributes` / `Request.Attributes` are
  `Dictionary<string, object>`, so System.Text.Json resolves every value by its
  *runtime* type. On net8.0+ the client routes bodies through the
  source-generated `DmartClientJsonContext`, which only ever registered
  `string`, `bool`, `int`, `long` and `double` — so a `decimal` money field, a
  `float`, a `short`/`byte`/`uint`/`ulong`, a `Guid`, a `DateTimeOffset`, an
  `int[]` or a `List<object>` all threw

  ```
  NotSupportedException: JsonTypeInfo metadata for type 'System.Decimal' was not
  provided by TypeInfoResolver of type 'Dmart.Client.Json.DmartClientJsonContext'
  ```

  at serialize time, before the request left the process. The netstandard2.1 leg
  is reflection-based and never had the problem, which is why this only ever bit
  modern consumers. The context now registers the closed set of JSON-representable
  scalars plus the common collection shapes. A consumer POCO still has to be
  handed over as a `JsonElement` — that is inherent to staying trim/AOT-safe.

- **`Dmart.Client` rewrote dictionary keys on the way out.** The
  source-generated context set `DictionaryKeyPolicy = SnakeCaseLower` alongside
  the property policy, so every key the caller chose was snake_cased before it
  left the process: an attribute stored as `myKey` arrived at the server as
  `my_key`, and read back as nothing under the name it was written with. Nested
  `Dictionary<string, string>` values had the same done to them. The server's
  `DmartJsonContext` sets no `DictionaryKeyPolicy`, so the two sides disagreed
  about the caller's own field names. The policy is gone; dictionary keys now go
  on the wire verbatim. `PropertyNamingPolicy` is unchanged — model properties
  are still snake_case, because those are a fixed shape both ends agree on.

- **Aggregation results were rounded on the way out.** `QueryService` narrowed
  every aggregation cell to a type the server's source-gen context knew —
  `long → int` and `decimal → double`. The same defect class as above, and both
  casts lost data. PostgreSQL emits `SUM`/`AVG` over numeric as `numeric`, which
  Npgsql hands back as `decimal`, so every money aggregate went through binary
  floating point: a `SUM` of `12345678901234567.89` returned
  `12345678901234568`, and `0.1 + 0.2` returned `0.30000000000000004`. The
  `long → int` cast was pure loss — `long` was already registered, and the cast
  silently wrapped any count past `int.MaxValue`. `DmartJsonContext` now
  registers `decimal` (and the remaining scalars, matching the client), and the
  casts are gone.

  **Wire shape is unchanged.** `AVG(numeric)` arrives from PostgreSQL at scale 16
  — the average of 10, 20, 30, 30 is literally `22.5000000000000000` — and
  `decimal` carries trailing zeros through System.Text.Json where `double` does
  not. Emitting it raw would have turned `22.5` into `22.5000000000000000`, so
  the scale is normalised away without altering the value. Callers keep seeing
  `22.5` and `90`; what changes is only that the value behind them is now exact.

- **`min` and `max` compared JSON numbers as text on PostgreSQL.** A jsonb path
  resolves through `->>`, which hands back text, so the two ordering reducers ran
  a string comparison: over amounts 9, 10 and 100, `min` answered `"10"` and
  `max` answered `"9"`. Wrong for any field whose values vary in digit count, and
  silent. PostgreSQL's default collation also ignores punctuation, so negatives
  misordered against decimals too.

  Both reducers now split the group by the value's actual JSON type, read off
  the jsonb form of the same path: one aggregate over the rows that really are
  JSON numbers, one over the rest, `COALESCE` picking which half answers.
  Numbers order numerically, text keeps its lexicographic order (ISO-8601
  timestamps depend on it), and the comparison uses `::numeric`, not the
  `::float` of the sort keys, so integers past 2<sup>53</sup> cannot tie. Both
  aggregates stream, so memory stays flat in group size.

  Reading the type rather than sniffing the extracted text is what makes it
  safe on strings that merely look numeric. `->>` erases the difference between
  the number `7` and the string `"007"`, so a regex sniff sends zero-padded
  codes — SKUs, account numbers, ISO 3166 numerics — down the numeric branch,
  where `::numeric::text` canonicalises `"007"` to `"7"`: an answer no row in
  the group holds. `jsonb_typeof` cannot make that mistake.

  On mixed data numbers order below text. That is **SQLite's** type ordering,
  not jsonb's own — jsonb ranks Number *above* String — and matching SQLite is
  the point, so the two backends answer the same instead of each inventing an
  answer. JSON nulls and absent fields stay out of both aggregates, as `->>`
  yielding SQL NULL already did.

  **SQLite was never affected** — its `->>` returns the JSON value's own SQL
  type, so it was already comparing numbers as numbers. `ISqlDialect` gained a
  `Reducer` overload carrying the field's JSON form alongside its text form; it
  has a default implementation delegating to the existing three-argument member,
  so third-party dialects are unaffected. Plain columns are untouched: they are
  already natively typed, and `MIN(updated_at)` still returns a timestamp.

- **A blank `otp_email_subject` override sent a blank Subject header.** The
  fallback to the `"OTP"` literal was `?? "OTP"`, which only fires on a missing
  key. An operator overlay at `~/.dmart/languages/<locale>.json` containing
  `"otp_email_subject": ""` — the natural way to write "no subject" by hand —
  went straight through to the mail server. Now guarded with
  `IsNullOrWhiteSpace`, which is what the comment above it always claimed.

- **`pruneEmptyFormValues` silently dropped `Date`, `File`, `Map` and `Set`.**
  The recursive branch tested `typeof value === 'object'`, which is true of
  every one of them, and `Object.keys()` on them is `[]` — so the function
  returned `undefined` and the value vanished from the payload with no error.
  Latent rather than live (today's schema forms only produce primitives, arrays
  and plain objects), but the first file-upload or date-valued field would have
  hit it. The branch now tests for a plain object by prototype.

### Changed

- **Wildcard policy expansion is capped at 256 exact tokens.** A policy with a
  wildcard resource type enumerates all 30 resource types — doubled, for the
  four-segment shape, across both `is_active` values — so a single
  `space:subpath:*:*` is 60 bind parameters, and an actor inheriting one per
  group multiplies that by their group count. Past the cap the remaining
  policies keep the LIKE form they always had, which matches exactly the same
  rows; only the spelling changes.

### Internal

- **The keyset-cursor index test now guards what its comment claims.** It
  sorted each `UNIQUE (...)` column list before comparing, so
  `UNIQUE (space_name, subpath, shortname)` would have passed while quietly
  restoring the per-batch full-table sort that `update_query_policies`' keyset
  cursor exists to avoid. The order is asserted as written, and both schemas
  are checked — `update_query_policies` runs against SQLite too, and the two
  DDL files are maintained separately.

- **`RETRIEVE_TOTAL_DEFAULT` documents its one exception.** Both the setting's
  comment and `config.env.sample` said an absent `retrieve_total` resolves to
  the setting. `/public/query` rewrites it to `false` before the query reaches
  `QueryService`, deliberately, so the setting never governed public traffic.

### Notes

- **Numbers are not reformatted anywhere in the stack**, and
  `NumberFidelityTests` now pins it: an integer written through
  `/managed/request` comes back out of create, update *and* patch as the same
  integer, exact past 2<sup>53</sup>. A field that reads back as `1000.0` was
  *stored* that way — `decimal` is the only .NET type that keeps trailing-zero
  scale through System.Text.Json (`1000.0m` serializes as `"1000.0"`), so a
  producer modelling an integral field as `decimal` is what puts that form in the
  store. Consumers mapping such a field onto `int` get a hard
  `JsonException: The JSON value could not be converted to System.Int32`;
  map it to `decimal`/`double` (matching a `"type": "number"` schema), or
  normalise it at the producer.

## v1.3.0 — 2026-08-25

### Migration required

- **Run `dmart update_query_policies --all-tables` as part of this upgrade.**
  Skipping it causes **access loss**, not degraded matching.

  `QueryPolicies.Generate` now emits the owner-unscoped literal
  (`{space}:{subpath}:{resource_type}:{is_active}`) unconditionally. It
  previously *replaced* that literal with an owner-group-scoped one whenever a
  row had an `owner_group_shortname`, so rows written before this release do
  not carry it.

  That matters because the new indexable filter rewrites a wildcarded
  permission into the exact strings a row can carry: `{key}:true:*` becomes
  exactly `{key}:true`, and `{key}:*` becomes `{key}:true` / `{key}:false`.
  Neither ever matches an owner- or group-scoped literal. So a caller whose
  permission carries an `is_active` condition, or no conditions at all, sees
  **zero** rows where they previously saw the group's — until the rows are
  rewritten.

  The command covers all six tables that carry `query_policies`: `entries`,
  `users`, `roles`, `groups`, `permissions`, `spaces`. `fix_query_policies` is
  **not** sufficient — it heals rows whose array is *empty*, and these rows
  have a stale non-empty one.

  **How long it takes.** Measured on a 2,000,000-row table in the worst case,
  where every row needs rewriting: **71 seconds**. The command pages by keyset
  over the `(shortname, space_name, subpath)` unique index, so cost is linear
  in rows — scale that figure by your row count rather than assuming it
  degrades. It is idempotent: a second pass updates 0 rows and returns in
  seconds, so it is safe to re-run if interrupted.

  That figure comes from a 24-core machine with PostgreSQL tuned so the table
  was fully cached (`shared_buffers=8GB`). **Rehearse against a copy of your
  own data before the maintenance window** — write throughput, not read cost,
  is what dominates here, and yours will differ.

  Rows are unreadable to wildcarded permissions until it finishes, so run it in
  the same maintenance window as the deploy, not after it.

### Performance

- **The read-time ACL filter is now indexable.** The two row tests in the
  visibility predicate were written in forms no index can serve — `unnest` +
  `LIKE` over `query_policies`, and `jsonb_array_elements` + `->>` over `acl`,
  both per-row subplans — so `idx_entries_query_policies_gin` and
  `idx_entries_acl_gin` were never used and every branch of the `OR` forced a
  sequential scan. They are now `&&` array overlap and `@>` jsonb containment,
  which the planner can combine under a `BitmapOr`.

  A caller's wildcard policies are expanded into the exact strings a row can
  carry. Anything that does not fit one of the three enumerable shapes is
  **not** guessed at and keeps the original `LIKE` test — narrowing a policy
  silently would deny access that should be granted.

  Measured end to end on a 2M-row folder, tuned PostgreSQL, 100 concurrent
  callers: **14.0 → 36.5 req/s**, mean **6773 → 2668 ms**, p99 **10006 → 4582
  ms**. Single request: **833 → 111 ms**.

  Two things worth knowing about where that gain comes from. It is **entirely
  in `COUNT(*)`** — with `retrieve_total` false both the old and new code serve
  ~10,400 req/s, identical within noise. And it is CPU work avoided rather than
  I/O: raising `shared_buffers` from 128 MB to 8 GB, enough to cache the whole
  table, moved the ratio by less than a factor of two.

- **The ACL predicate is skipped entirely when it is a tautology** — when the
  caller's permissions already cover every row the query can reach, the
  predicate can only cost a scan. `entries` only.

- **Planner statistics for `(space_name, subpath)`** ship in the schema
  (`entries_space_subpath_stx`, and the equivalent on `attachments`).
  PostgreSQL assumed the two columns were independent and multiplied their
  marginal selectivities; on a real instance that under-estimated one folder by
  **6.9x**, which was shaping every plan on the largest table.

### Added

- **`RETRIEVE_TOTAL_DEFAULT`** decides what a query means when it omits
  `retrieve_total` entirely. The field is tri-state: an explicit `false` always
  skips the count, an explicit `true` always performs it, and *absent* now
  resolves to this setting. Defaults to `true`, which is the existing
  Python-parity behaviour, so nothing changes unless you set it.

  Counting is what the request costs: on the same 2M-row folder at 100
  concurrent, the endpoint served 36.5 req/s with the count and **10458 req/s**
  without it. `QUERY_TOTAL_CAP` bounds that work; this removes it for callers
  that never asked.

  **Before setting it false:** when the count is skipped, `total` is reported
  as **-1** — not 0, and not absent. Set it only once your clients either send
  `retrieve_total: true` where they need a count, or ignore `total` entirely.

- **`dmart update_query_policies --all-tables`** widens the recompute from
  `entries` to every table carrying `query_policies`. The default scope stays
  `entries` for Python parity.

- **OTP emails carry a localized subject.** `otp_email_subject` resolves
  through the same `LanguageLoader` path as the message body, so it is served
  in the recipient's language and stays operator-overridable at
  `~/.dmart/languages/<locale>.json` without a rebuild. English, Arabic and
  Kurdish ship. The code is deliberately **not** substituted into the subject —
  that would leak it to lock-screen notification previews and mail-server logs.

### Fixed

- **`groups` was reachable by no backfill command.** Six tables carry
  `query_policies`; `fix_query_policies` listed five, omitting `groups`, and
  `update_query_policies` was `entries`-only. A `groups` row with an owner
  group therefore had no path to gain the literal the new filter needs. Both
  commands now cover it.

- **`ISqlDialect.ArrayOverlapAny` has a default implementation.**
  `Dmart.QueryGrammar` is a published package, so adding it as a bare abstract
  member would have broken every third-party dialect at compile time. The
  default delegates to `ArrayAnyLike` — same rows, not indexable; both in-tree
  dialects override it.

- **`PermissionFilter.Append` keeps a five-parameter overload.** Adding
  optional parameters to a published API is source-compatible but not
  binary-compatible: C# bakes optional-argument values into the *caller's* IL,
  so an assembly built against the old signature would throw
  `MissingMethodException` while still compiling from source.

- **`update_query_policies` no longer degrades quadratically.** It paged with
  `LIMIT/OFFSET` ordered by `(space_name, subpath, shortname)`, which matches no
  index — so every batch sorted the whole table to disk and discarded
  everything before the offset, at 1575 ms per 1000 rows. Because both the
  per-batch cost and the batch count scaled with table size, a 23M-row table
  projected to roughly two days. It now pages by keyset over the
  `(shortname, space_name, subpath)` unique index every affected table already
  carries: **0.216 ms per batch, and 71 s for the same 2M-row migration that
  previously projected to ~22 minutes.** This matters because the migration
  above is not optional.

- **The skipped-count sentinel no longer reaches page arithmetic in cxb or
  catalog.** Every read of `total` used an idiom that misses `-1` — `?? 0` does
  not catch it because it is not nullish, and `|| records.length` does not
  because `-1` is truthy — so it flowed into pagers that rendered "1 of 0
  pages". All 12 read sites now go through `ui-shared/query-total.ts`.

- **Empty form values are pruned before create in catalog**, so a schema-driven
  form no longer submits blank strings and empty objects the server then
  rejects.

### Known issues

- **`ORDER BY updated_at DESC LIMIT n` still defeats the ACL indexes when the
  predicate is selective** ([#213](https://github.com/edraj/csdmart/issues/213)).
  The planner drives the page fetch from `idx_entries_updated_at` and applies
  the visibility predicate as a filter, walking the whole table. Measured at
  1512 ms against 0.069 ms for the same predicate when the indexes are used.
  This release does not address it; the gains above are in `COUNT(*)`.

## v1.2.9 — 2026-08-24

### Security

- **The .NET runtime compiled into the binary is now checked for CVEs, in the
  shipped artifact.** dmart publishes self-contained with `PublishAot`, so the
  runtime lives inside `/usr/bin/dmart` and a user cannot patch it by updating
  their distro's .NET. It was also invisible to every existing check: the
  runtime pack is SDK-injected rather than a `PackageReference`, so it appeared
  in neither `dist/deps/*.lock.json` nor the CycloneDX SBOM.

  **v1.2.7 and v1.2.8 shipped runtime 10.0.10**, carrying CVE-2026-62901
  (HIGH, denial of service) plus CVE-2026-62899 and CVE-2026-62909. **v1.2.9 is
  the first release built on runtime 10.0.11**, where all three are fixed.

  The check reads the runtime version out of the finished binary — the AOT
  publish, the binaries inside both the Fedora and EL9 RPMs, and each release
  tarball — and refuses to pass if it cannot determine one. Scanning the build
  tree instead would have described the toolchain rather than the artifact:
  every artifact here is produced by a different SDK (the runner's own for the
  Fedora RPM, a container's for EL9 and the tarballs, a floating one for
  Windows and macOS), and none of them is `dist/LOCKFILE_SDK`.

- **CycloneDX SBOMs now list the runtime packs.** `Microsoft.NETCore.App.Runtime.<rid>`
  and `Microsoft.AspNetCore.App.Runtime.<rid>` are compiled into the binary and
  ship inside it, but were absent from every SBOM — v1.2.7's listed 467
  components and neither of them. The versions come from MSBuild rather than
  being assumed to follow the SDK, and generation now fails rather than emit an
  SBOM that omits the runtime.

- **The EL9 builder container no longer freezes its toolchain.** It installed
  the SDK once, at creation, and never looked again — a builder created
  2026-08-15 was still on `dotnet-sdk 10.0.110` in late August, so every EL9
  RPM built in between shipped the vulnerable runtime. The SDK is now refreshed
  when an existing container is reused.

### Performance

- **The planner is told that `space_name` and `subpath` correlate.** Every
  query's `WHERE` leads with the pair, and PostgreSQL was estimating it as two
  independent selectivities — but a subpath belongs to exactly one space, so
  `/orders` occurs only inside `purchase`. Measured on a 22.7M-row instance,
  `purchase/orders` was estimated at 375,397 rows against an actual 2,589,782:
  **6.9x low**. With extended statistics it estimates 2,560,137, an error of
  1.1%.

  This is plan quality, not counting. A 6.9x underestimate on the largest table
  shapes join order, scan choice and memory sizing for every query that touches
  it. Upgrades pick it up at the next autovacuum `ANALYZE`, or immediately with
  `ANALYZE entries; ANALYZE attachments;`.

### Documentation

- **`QUERY_TOTAL_CAP` is documented in `config.env.sample`.** The setting that
  bounds a pagination count shipped in v1.2.8 without a line in any sample an
  operator would read. Both sample configs are now pinned against
  `DmartSettings` by tests, because an unrecognised key there is not a soft
  failure: dmart exits on it, so a stale key in the sample hands an operator a
  file that refuses to boot, and one in the packaged config breaks a fresh
  install.

### CI

- **Superseded pull-request runs are cancelled instead of queueing.** Neither
  workflow declared a concurrency group, so every push started a full run and
  the obsolete ones kept their runners. Pushes to `master` still run to
  completion — that run is the record for a commit that has already landed.

- **The one required status check moved to a hosted runner.** `build-and-test`
  reads a `needs` result and echoes it — about six seconds — but was pinned to
  the self-hosted pool, where it queued behind 8-10 minute build jobs and lost
  the race to every newer run. Merges were waiting twenty minutes on an `echo`.

## v1.2.8 — 2026-08-23

### Performance

- **A query's `total` no longer counts every matching row.** `total` is a
  pagination count and counting is O(matching rows) whatever the indexes look
  like. On a production instance one subpath holds 2,589,782 rows, and every
  page request re-counted all of them: an Index Scan over 2.59M entries with a
  heap visit each, 558,866 buffer hits (~4.4 GB), 2,435 ms warm — and far worse
  under concurrency, where it produced a p50 of 17s and thousands of client
  cancellations in an hour.

  With the new `QueryTotalCap` setting above 0, the count is emitted as
  `SELECT COUNT(*) FROM (SELECT 1 FROM t WHERE <filters> LIMIT cap+1) c`, so the
  scan stops as soon as `cap+1` rows qualify. Measured on that same production
  table: **2,435 ms → 29 ms, 558,866 → 10,006 buffers.**

  `QueryTotalCap` defaults to **0 (unlimited)**, which is byte-identical to the
  previous behaviour and preserves Python parity — a deployment must opt in.
  Above the cap the response reports `total` as the cap AND sets
  `total_is_lower_bound`, because a client reading a clamped total as exact is
  the failure this would otherwise introduce. The `LIMIT` is applied after the
  ACL predicate, so a cap can never count rows the actor cannot see.

### Security / CI

- **The self-hosted security gate now actually runs all three scanners.** Steps
  execute under `bash -e`, so a non-zero exit from gitleaks/trivy/semgrep
  aborted the step at the scanner invocation: the `rc=$?` capture that followed
  was dead code, the remaining scanners were skipped, and the gate's own result
  step never reported. Scanner status is now captured with `|| rc=$?`, and the
  gate result runs even after a failed scanner.

- **`.gitleaks.toml` allowlist paths are anchored.** gitleaks matches path
  regexes as unanchored substrings, so `README\.md$` exempted all eleven READMEs
  in the tree rather than the intended root one, and `dmart.Tests/` and `seed/`
  matched at any nesting depth. A credential pasted into a nested README would
  have passed the gate silently.

- **The .NET dependency graph is scanned for CVEs.** trivy detects NuGet by the
  filename `packages.lock.json`; this repository deliberately keeps that content
  as `dist/deps/<slug>.lock.json`, outside the build, so trivy walked past every
  .NET dependency and reported only the JavaScript lockfiles. 93 NuGet packages
  across five projects had never been checked against a vulnerability database.
  The gate now materialises the recorded graph under the expected name — outside
  the worktree, since a `packages.lock.json` in the tree is what breaks the
  distro builders — and scans it. No findings today.

- **trivy no longer scans its own binaries.** The gate downloads gitleaks and
  trivy into `.cigate/`, which was neither gitignored nor skipped, so trivy's
  gobinary analyzer reported their embedded Go stdlib CVEs — fixable ones, which
  `--ignore-unfixed` does not suppress.

- **Semgrep is version-pinned**, and its exit codes are distinguished: 1 means
  findings, ≥2 means the scanner itself failed. Both fail the gate, but a
  crashed scanner is no longer reported as "found security issues".

## v1.2.7 — 2026-08-22

### Fixed

- **The release's aggregate `SHA256SUMS-all` job can find the release again.**
  Its "download every asset" step ran `gh release download` with no repository
  context and failed on v1.2.6; the step now passes `GH_REPO`, so the signed
  aggregate checksum manifest is produced with the rest of the artifacts.
- **The query-search feature-matrix timestamp test no longer fails on non-UTC
  machines under the SQLite driver.** The fixture stamped rows with
  `DateTime.UtcNow` while dmart's timestamps are naive LOCAL wall clock
  (`TimeUtils.Now()`); SQLite's lexicographic text comparison exposed the
  offset, while PostgreSQL masked it through the session-timezone coercion.
  Test-only fix, plus new regression pins (`SqliteTimestampRangeTests`) that
  hold the SQLite timestamp storage format, the epoch-ms bound expression,
  and the server binding path together.
- **An empty `filter_tags` set emits a safe constant-false predicate.** The
  PostgreSQL containment seam produced an empty `()` for a zero-length value
  list (a syntax error); the sole caller guards on a non-empty set, but the
  seam now returns `FALSE`, matching the SQLite dialect which already did.

### Performance

- **`@tags:` / `@roles:` / `@groups:` searches are now index-served.** The
  positive emission used to OR the containment with a `jsonb_typeof`-guarded
  object-ILIKE fallback; PostgreSQL can only BitmapOr an OR whose every arm is
  indexable, so the fallback arm forced a **sequential scan** on each such
  search. Positives now emit one bare `col @> '["x"]'::jsonb`, served straight
  from the existing `jsonb_path_ops` GIN indexes. Semantics note: a row whose
  tags/roles/groups column holds a JSON *object* (a shape the models never
  write) no longer substring-matches. Negated selectors keep the old emission
  (NOT-containment can't use an index anyway).
- **`filter_tags` no longer sequential-scans.** It compiled to `tags ?| $1`,
  but `?|` is not in the `jsonb_path_ops` operator class, so
  `idx_entries_tags_gin` never served it. It now compiles to
  `(tags @> '["a"]' OR tags @> '["b"]')` — equivalent for arrays of strings —
  which the GIN index serves as a BitmapOr.
- **Composite `(space_name, subpath)` indexes** on `entries` and `attachments`
  replace the single-column `space_name` indexes (whose leading-column role
  the composites cover). Every query's WHERE leads with exactly this pair.
- **Npgsql automatic statement preparation** (`DATABASE_MAX_AUTO_PREPARE`,
  default 200): the hot statements were parsed and planned by PostgreSQL from
  scratch on every execution.
- **Creates issue three fewer SQL statements.** The duplicate-shortname probe
  went typed-then-untyped, and the typed leg always misses on a create; it is
  now a single untyped lookup. The parent folder consulted by the uniqueness
  gate and the folder-content gate was loaded twice, identically, on two
  connections; it is now loaded once and shared.
- **Opt-in auth read cache** (`AUTH_CACHE_TTL`, default 0 = off): caches the
  per-request user row + session-validity pair for the configured seconds.
  Off, behavior is unchanged. On, single-node revocations still take effect
  immediately (writes evict), and other replicas converge within the TTL.
- **`JsonbHelpers.EnumMember` no longer reflects per call** — the
  `[EnumMember]` map is built once per enum type; the helper runs several
  times on every request.

## v1.2.6 — 2026-08-22

### Security

- **Frontend dependency advisories cleared.** `yarn audit --groups dependencies`
  flagged esbuild (<0.25.0, GHSA-67mh-4wv8-2f99) and @tootallnate/once (<2.0.1)
  in the embedded cxb/catalog SPAs; both are pinned forward via `resolutions`.
  The audit is now clean and both SPAs still build.

### Changed

- **The published SBOM now covers the embedded frontends.** dmart compiles the
  cxb and catalog Svelte SPAs into the AOT binary, so their npm dependencies
  ship inside the executable. `dist/frontend-sbom.sh` reads them from `yarn.lock`
  (the resolution the build installs from) and merges them into every per-RID
  CycloneDX document — the SBOM went from the .NET graph alone to the full
  server-plus-frontend inventory.

## v1.2.5 — 2026-08-18

### Security

- **`filter_fields_values` now constrains every branch of a caller's search.**
  The permission clause was concatenated onto the caller's expression as bare
  tokens, giving it no special standing in the grammar. Because AND binds
  tighter than OR, a caller-supplied `or` split the expression and left the
  clause governing only the right-hand branch — `(k=v) OR (k=w AND dept=sales)`,
  where the left side is reachable without satisfying the permission. A second
  route needed no boolean keyword at all: an alternation on the constrained
  field (`@dept:sales|ops`) accumulated into the permission's own selector,
  yielding `dept IN (sales, ops)` and returning exactly the rows the restriction
  existed to hide. The caller's search is now parenthesised before the clause is
  appended, and unbalanced parens are normalised first so a stray `)` cannot
  close the wrapper early. The query-policy gate is a separate clause and always
  held, so this widened a row-level field restriction inside an
  already-granted subpath rather than reaching ungranted rows.

  One behaviour change worth knowing: negating the field a permission
  constrains (`-@dept:sales` under an FFV of `@dept:sales`) now returns nothing
  instead of every `sales` row. The two used to land in one leaf run where the
  last sign won and the caller's negation was silently discarded.

### Fixed

- **`@query_policies:…` searches no longer fail on SQLite.** The text-array
  predicate referenced the bare iteration alias, which resolves under
  PostgreSQL's `unnest` (a column) but not SQLite's `json_each` (a table), so
  every such search raised `no such column: elem`.
- **Array searches with a numeric value no longer abort on a non-numeric
  element.** Elements of a scalar array are text, and the cast was applied to
  all of them, so `-@tags[]:100` over `["red","blue"]` failed the whole query on
  PostgreSQL. Guarded for the equality, comparison and `BETWEEN` forms.
- **A plugin that fails to load is now visible.** The scan runs before the
  logger exists, so a failure produced one line on stderr and startup carried
  on — a deployment that lost a plugin looked completely healthy, and the only
  symptom was behaviour that quietly stopped happening. Failures are now
  replayed through the logging pipeline at Error with a summary line, and
  reported by `GET /info/plugins` as records with `status: "failed"` and a
  `reason`. The silent case is covered too: a plugin directory holding a
  `config.json` but no binary (missing or misnamed) used to be skipped without
  a word.

- **Repeated selectors are no longer collapsed across `or` or paren groups.**
  Deduplication is a cosmetic shortening, but across a boolean it moved a
  restriction rather than shortening it, and could drop an injected permission
  token outright.

### New

- **`MAX_PASSWORD_RECORDS_PER_REQUEST`** (default 50) bounds how many records in
  one `/managed/request` may carry a password. Each costs an Argon2id hash at
  m=100 MB, and the batch was otherwise unbounded. Records without a password
  are not counted; `0` disables the check.
- `/managed/request` accepts `password` when creating a user, validated against
  the password rules and hashed with the shared hasher. The update path still
  rejects it.

### Documentation

- The native-plugin `config.json` example in `README.md`, `docs/plugins-and-mcp.md`
  and `docs/contributing.md` was unusable. `"subpaths": ["__ALL__"]` is the
  legacy flat form, which dmart rejects at load with a migration error, and
  `"schema_shortnames": ["__ALL__"]` is matched as a literal schema name — so
  even after fixing the first, the plugin would load and never fire. Both now
  match the shipped samples: a `{ "__all_spaces__": ["__all_subpaths__"] }`
  dict, and an empty list to mean every schema.
- `docs/query.md` documents array-field predicates (including that `-@` makes a
  value-level operator inert) and same-field accumulation — the contracts the
  query-search regression tests defend.

## v1.2.4 — 2026-08-17

**User deletion is now soft by default.** Deleting a user no longer removes the
row: it stays so foreign keys keep resolving, marked deleted, with email,
msisdn and password cleared. Nothing the user owns is touched.

### New

- **`USER_DELETION_MODE`** — `"soft"` (default) or `"hard"`, applied uniformly
  to self-delete (`POST /user/profile/delete`) and admin delete. Hard mode is
  the previous behaviour: the row and everything the user personally owns go,
  and structural objects they owned (spaces, roles, groups, permissions, other
  users) are reassigned to the `dmart` sentinel. Histories are never deleted in
  either mode.
- Two columns on `users`: `is_deleted` and `deleted_at`. Added automatically on
  upgrade for both backends.

### Behaviour

- **A deleted account cannot log in, refresh, or be edited.** The check is
  `IsUsable` (`is_active && !is_deleted`), applied at JWT validation, WebSocket
  upgrade, OAuth refresh, OTP request and password-reset-confirm. `is_active`
  alone would have let a password reset revive a deleted account, since soft
  delete does not touch it.
- **Login is anti-enumerating**: a deleted account gets the generic "invalid
  username or password", never "account locked" — which would imply
  recoverable, and would confirm the account existed.
- **Creating a user with a soft-deleted shortname resurrects the name.** Soft
  delete ends the ACCOUNT, not the NAME. Without this the shortname was
  unusable forever — create refused it as taken, update refused it as deleted —
  which would have stranded system accounts like `anonymous`. The create writes
  every other column, so nothing survives from the deleted account but the
  name.
- **`force` still applies in hard mode.** Deleting a user who has created
  records is refused unless `force=true`, exactly as before this release. The
  mode picks soft-vs-hard; `force` answers "yes, I know this user owns records".
  Soft mode ignores it, having nothing to guard.
- **Soft delete writes one history row**, recording who did it and what changed
  (`is_deleted` false→true, `email` old→null). Hard delete still writes none —
  there the row is genuinely gone.

### Upgrade note

Deleting a user is **irreversible**. Nothing sets `is_deleted` back to false
except creating a new account under the same shortname; both upsert paths pin
the flag to its existing value precisely so an unrelated write cannot revive an
account by accident. If you want the old destructive behaviour, set
`USER_DELETION_MODE="hard"`.

## v1.2.3 — 2026-08-16

Packaging and CI only — no changes to dmart itself. The container image is
rebuilt on a different base, so it is worth taking.

### Container

- The container image now **installs the Alpine package** instead of compiling
  dmart in a `dotnet/sdk:10.0-alpine` stage. That stage was a second
  `linux-musl-x64` AOT build of exactly what the APK job already produces —
  ~5 minutes of a shared 3-runner pool on every release, for a byte-identical
  binary. The image now ships the same artifact an Alpine user installs, so it
  doubles as a test of that package, and the release job smoke-runs it before
  pushing.
- The container base is pinned to **`alpine:3.24`**, matching the Alpine the
  binary is compiled against (`dotnet/sdk:10.0-alpine` is 3.24 / musl 1.2.6).
  It was `alpine:edge` — a rolling pre-release whose musl can drift ahead of
  the compiler's — with no recorded reason. `Dockerfile.runtime` is pinned to
  the same base.

  **If you run the image:** the base moved from a rolling pre-release
  (`3.25.0_alpha`) to Alpine 3.24, so the OS packages inside it change version
  accordingly. dmart itself is byte-identical to 1.2.2 — same binary, same
  behaviour.

### CI

- CI now **builds the container image and serves it**, on packaging changes and
  on every push to master. The image was previously built only by the release
  workflow, so a broken Dockerfile or package layout reached a tag before
  anyone saw it. Serving it matters more than building it: with
  `libe_sqlite3.so` removed from the image, `podman build` still succeeds and
  only the readiness check catches it.

## v1.2.2 — 2026-08-16

A performance release for the Parquet export, plus one cleanup-command change.

**Note for anyone comparing archives across this upgrade:** attachment archive
bytes differ from earlier releases. The streamed reader emits PostgreSQL's JSON
key order where the old one emitted C#'s, so the same attachment produces
different — equivalent — text. Verified by importing both archives into fresh
databases and diffing all 60,000 restored rows: identical. Only a byte-level
comparison of the archives themselves will notice.

- Parquet export now streams **histories and attachments** through `COPY` as
  well as entries, and stops parsing their JSON columns into objects only to
  serialise them straight back. Attachments **16x faster** (4131 ms to 258 ms on
  60,000); histories take a full-space export of 21,843 entries + 40,000
  histories from 350 ms to **304 ms**, and unlike the entries change this one
  pays at any size. Media bytes are still fetched per row — streaming them
  inline would hold every blob in memory at once. Same column-type guard and
  fallback as the entries reader.

- `prune-empty-histories` now **deletes** rows with a NULL diff instead of
  reporting and skipping them. A NULL predates the `{}` convention but means the
  same thing — an audit row recording no change — so leaving them behind meant
  the cleanup only half-worked. The count is still broken out separately.

- Parquet export reads entries through a streaming `COPY` on PostgreSQL instead
  of walking the table with `LIMIT/OFFSET`. **2.6x faster on 218,430 entries**
  (4034 ms to 1571 ms); no measurable change at 21,843, where three pages leave
  nothing to win. `OFFSET` makes PostgreSQL scan and discard everything before
  it, so the paged reader is quadratic in table size while the streamed one is
  linear — the gap widens as the install grows. Guarded by a column-type check
  against the live catalog; on a mismatch it falls back to the paged reader with
  a warning rather than failing, because a schema change should make an export
  slower, not impossible.

## v1.2.1 — 2026-08-15

A patch release: one new maintenance command, and the RPM build repaired.

### New

- **`dmart prune-empty-histories [--space <name>] [--dry-run]`** — deletes
  history rows whose `diff` is an empty object. Those are audit records that
  nothing changed, written before the empty-diff append was fixed in 1.2.0; no
  current writer produces them. Run it **once** after upgrading.

  Deletes are **tombstoned**, so an incremental Parquet consumer learns the rows
  are gone rather than silently keeping them — which means a large prune writes
  as many rows into `deletions` as it removes, and `prune-tombstones` drains
  those once your increments have caught up. Rows with a NULL diff are a
  different, older shape and are **reported rather than removed**.

- **`docs/maintenance.md`** — operator guide for both prune commands, including
  the one thing neither of them says on its own: nothing runs them for you.
  There is no scheduler and no background service; they do what you ask, when
  you ask.

### Fixed

- **The RPM build.** Two bugs, both introduced with the 1.2.0 SQLite packaging
  work, failed the RHEL 9 and Fedora jobs of the 1.2.0 release build:
  a `%files` entry stranded inside `%install` (so rpm's shell tried to *execute*
  a path), and `libe_sqlite3.so` never staged into the source tarball while
  `%files` lists it unconditionally.

  **The RPMs published on the v1.2.0 release are not affected** — they were
  built with these fixes applied and attached by hand, and their binaries report
  `v1.2.0-0-g832bbbe`. This release makes the build work from a clean checkout
  again.

### CI

- CI now **builds the Fedora RPM** on every push and asserts its payload. Both
  1.2.0 packaging bugs were invisible to CI because RPMs were only ever built by
  the release workflow, so a broken spec could sit on master for a whole release
  cycle. A parse check would not have caught either — `rpmspec -P` reports the
  spec as valid — so the job does a real `rpmbuild`.

## v1.2.0 — 2026-08-15

Two large pieces of work land here: **SQLite as a second database backend**,
and **Parquet as a backup/restore format**. Everything else is fixes and
packaging.

### SQLite backend

dmart now runs on SQLite as well as PostgreSQL. Set `DATABASE_DRIVER=sqlite`
(inferred when unset, from whichever connection settings are present).

This is a **tier, not parity** — the target is development, CI, single-node and
edge deployments. PostgreSQL remains the production backend, and its code path
is unchanged: the same SQL is emitted and the same API responses come back.

- All repositories, the search grammar, aggregations, joins and sorts now emit
  through an `ISqlDialect` seam rather than PostgreSQL-specific SQL.
- SQLite-specific handling where the engines genuinely differ: `hstore` mapped
  onto JSON for OTP, a lock strategy that does not depend on `xmax`, `text[]`
  reads, provider-neutral scalar reads, and an FTS5 trigram index (declined for
  non-ASCII, where it cannot work — JSON columns are stored as literal UTF-8 so
  SQLite can index Arabic).
- `dmart import` rebuilds the SQL store from the flat files under
  `SPACES_FOLDER` on SQLite too. The storage design's premise is that the SQL
  store is a rebuildable index; that was previously unbacked on this tier.
- SQLite errors are classified at the HTTP boundary, so they surface as the
  same envelopes PostgreSQL produces.
- CI runs as a **driver matrix** — the whole integration suite against both
  backends — and publishes a Native AOT binary on every push.
- 17 tests skip on SQLite, each gated with a stated reason rather than weakened
  to pass everywhere: query-plan assertions, GIN and server-notice
  observability, the SDK adapter (scoped PostgreSQL-only by the audit), and the
  PostgreSQL-only fast-import path. Nothing fails on SQLite.

See `docs/sqlite-backend-audit.md`.

### Parquet export and import

A columnar backup format alongside the existing zip. Written by hand — no
library meets the 100%-AOT rule — and verified against pyarrow in both
directions.

```
dmart export <space> --parquet [--subpath <p>] [--since <dir>] [--output <dir>]
dmart export --all --parquet                   # full backup, verified
dmart import <dir> --parquet [-r] [--no-verify] [--drop-indexes]
dmart prune-tombstones --older-than <days> [--dry-run]
```

- **Every table**: entries, attachments, histories, spaces, users, roles,
  permissions, and a deletions (tombstone) table.
- **Scope**: one space, one subfolder, or everything. Scoped exports
  deliberately omit users/roles/permissions — the users table holds password
  hashes, and writing those to disk should follow from asking for a backup, not
  from exporting one folder.
- **Attachment media** is stored content-addressed as
  `blobs/<sha256[0:2]>/<sha256>`, so an unchanged attachment ships zero bytes
  in an increment and identical files are stored once.
- **Incremental** via `--since <previous-export-dir>`, with tombstones so a
  deletion is not indistinguishable from "unchanged".
- **Verified on write** (`--all`) and **on restore** (now the default).
- **Hive-partitioned** (`space_name=<s>/`), so DuckDB and Spark read it
  directly: `read_parquet('entries/**/*.parquet', hive_partitioning=true)`.
- Bulk restore reuses the zip importer's COPY path — 54× faster for entries,
  64× with history included; the user restore is batched and shares the SQL
  clause that preserves existing password hashes.
- `--drop-indexes` drops the GIN indexes for the load and rebuilds them after
  (PostgreSQL only). A large-restore lever: it *costs* a few percent below
  ~200k rows.

See `docs/parquet-export-design.md` and `bench/REPORT-backup-formats.md`.

### Behaviour changes

- **`import --parquet` now verifies by default.** `--no-verify` opts out;
  `--verify` is still accepted. Previously export verified unless you opted out
  while restore verified only if you opted in, which put the weaker default on
  the more dangerous operation.
- **A partial zip export no longer exits 0.** A backup pipeline reads the exit
  code, not the wording.
- **A zip export that drops attachment media now warns.** Zip names media after
  `payload.body`; an attachment with bytes but no such filename exports its
  metadata and not its bytes. That behaviour is unchanged and deliberate, but
  it is no longer silent. Parquet has no such hole.
- **PostgreSQL session timezone is pinned to the app host's.** dmart stores
  local-naive timestamps, and columns defaulting to `NOW()` were being stamped
  in the *server's* zone. Rows written before this fix cannot be repaired — see
  the upgrade note below.

### Fixes

- Export silently truncated at 100,000 rows.
- Export buffered the whole archive in memory; it streams now.
- Aggregation reducers emitted invalid SQL on SQLite.
- `db_size_info` answered dishonestly on SQLite.
- Three clock bugs in incremental export: a UTC watermark compared against
  local-naive columns, `deletions.deleted_at` relying on the server's `NOW()`,
  and a manifest mixing the two.
- Folder-content violations were untranslated for SQLite.
- Three defects that blocked building and running the container image.
- History: skip the append when the diff is empty.

### Packaging and container

- The RPM, deb and apk packages ship a **SQLite-backed default config**, so an
  install runs without a database server.
- The container image **drops PostgreSQL** and runs dmart alone on SQLite.
  See `docs/container.md`.
- CI gives each job its own smoke port instead of scanning for a free one.

### Upgrade note

**Do not chain a Parquet increment across this upgrade.** `updated_at` defaults
to `NOW()`, which the database server evaluated in *its* timezone; on a UTC
server under a +03 host, rows written before the timezone pin are stamped three
hours behind every host-local watermark, so an increment can read them as older
than they are and skip them.

No migration can repair this — a stamp three hours low is indistinguishable
from a row genuinely written three hours earlier. After upgrading, take **one
full export** and start the increment chain from it. Increments taken wholly
after that point are unaffected.

Separately, `deletions` is append-only and was never pruned before this
release. If it has grown, `dmart prune-tombstones --older-than <days>` bounds
it — choose a window **longer** than your incremental export interval.

## v1.1.5 and earlier

See the git history.
