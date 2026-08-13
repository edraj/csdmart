# The container image

`admin_scripts/docker/Dockerfile` builds a **single-process Alpine image**:
dmart, AOT-compiled, storing its index in SQLite. It publishes to
`ghcr.io/edraj/csdmart`.

```
podman build -t dmart -f admin_scripts/docker/Dockerfile .
podman run --name dmart -p 8000:8000 -d dmart
podman exec -it dmart dmart passwd dmart     # admin ships passwordless
```

`./admin_scripts/docker/notes.sh` is the developer loop: it builds the musl
binary in a persistent builder container and assembles
`Dockerfile.runtime` from it, which is much faster to iterate on than the
self-contained `Dockerfile`. The two share `entrypoint.sh`, so a change to
first-run behaviour affects both.

## What is inside, and what is not

| | |
|---|---|
| Storage | SQLite at `/root/.dmart/dmart.db`, beside the spaces folder so a backup of one directory captures both |
| Processes | one — `dmart` runs as PID 1 |
| Size | ~72 MB |
| Not included | **PostgreSQL**. Earlier tags bundled PostgreSQL 18, ran `initdb` on first start and supervised two processes |

The SQLite tier is deliberately reduced — see the readme's *Storage backends*
section and [sqlite-backend-audit.md](./sqlite-backend-audit.md) for where it
stops. For anything past that, point the container at an external PostgreSQL:

```
podman run -e DATABASE_DRIVER=postgresql \
           -e DATABASE_HOST=db.internal -e DATABASE_NAME=dmart \
           -e DATABASE_USERNAME=dmart   -e DATABASE_PASSWORD=… dmart
```

`krb5-libs` stays in the image for exactly that path: the PostgreSQL *server*
is gone, but Npgsql is still compiled into the binary and its Kerberos support
needs the library. (`DOTNET_SYSTEM_NET_SECURITY_DISABLEGSSAPIPAL=true` may make
it redundant — that is a separate, testable trim, not something to infer.)

## Upgrading from a PostgreSQL-era image

A container that reuses a volume from one of those images **refuses to start**,
and prints how to proceed. That is deliberate. Starting anyway would come up on
an empty SQLite file and serve nothing — with `/health/ready` returning 200,
because it would be probing the new empty database quite successfully.

Nothing is lost. The flat files under `SPACES_FOLDER` are the source of truth
and the SQL store is a rebuildable index over them, so the migration is:

```
# 1. start without the PostgreSQL data dir mounted
# 2. rebuild the index from the flat files
podman exec -it dmart dmart import /root/.dmart/spaces
```

The other two routes are to point at an external PostgreSQL (above), or to
discard the old volume and start clean.

## First run

`entrypoint.sh` initializes only when `/root/.dmart/.db_initialized` is absent:

- generates `config.json` (CXB branding) and appends to `config.env` a random
  `JWT_SECRET`, `DATABASE_DRIVER=sqlite` and an absolute `SQLITE_PATH`
- `chmod 600 config.env` — it holds the generated secret, and the container's
  default umask leaves it world-readable, which dmart itself warns about
- prints the `dmart passwd` command, because the admin is created passwordless

`DATABASE_DRIVER` is written explicitly rather than left to inference. The file
survives image upgrades and gets hand-edited, so adding a PostgreSQL connection
later must also mean flipping the driver line — a connection edit alone should
never move a deployment between backends.

The entrypoint `exec`s dmart rather than backgrounding it and trapping: with
nothing left to supervise, dmart becomes PID 1 and receives `SIGTERM` directly,
so `podman stop` runs its own lifetime hooks instead of a trap racing a second
process.

## Build pitfalls

Three things broke this build in ways that were invisible from CI, because the
release job always builds a tagged version from a clean checkout. Worth knowing
before adding to the Dockerfile:

**Do not name a build arg after an MSBuild property.** MSBuild promotes
environment variables to properties, so `ARG VERSION=unknown` becomes
`$(Version)` inside `dotnet restore` — and `unknown` is not a parseable
version. Restore then dies 16 ms in with

```
error MSB4181: The "RestoreTask" task returned false but did not log an error.
```

which names neither the property nor the value. The build arg is
`DMART_VERSION` for this reason. `Configuration`, `Platform`, `TargetFramework`
and `OutputPath` are the same hazard.

**`.dockerignore` patterns are anchored at the context root.** A bare `obj/`
does *not* match `Dmart.Models/obj/`, so a developer who has run `dotnet build`
locally ships host `obj` directories into the context; `COPY . .` then drops a
host-SDK `project.assets.json` on top of the one the container's own restore
just wrote, and publish fails with `NETSDK1064` naming an ILLink.Tasks version
the image never restored. Hence `**/bin/` and `**/obj/`.

**The SQLite engine needs a sidecar.** SQLitePCLRaw reaches its native library
through `dlopen`, which Native AOT cannot link in, so the publish output emits
`libe_sqlite3.so` next to the binary and every packaging path has to place it
(the RPM, deb and apk specs all do). Copying only `/out/dmart` produces an image
that starts and then dies inside `SqliteConnection`'s static constructor before
serving anything.

## Verifying a build

```
podman run -d --name t -p 8123:8000 dmart
curl -s localhost:8123/health/ready            # {"status":"success"}
podman exec t ps -o pid,args | head -2         # dmart at PID 1
podman logs t | grep 'database driver'         # sqlite (DATABASE_DRIVER)
podman exec t sh -c 'printf "Test1234\nTest1234\n" | dmart passwd dmart'
DMART_URL=http://127.0.0.1:8123 DMART_ADMIN=dmart DMART_PWD=Test1234 ./curl.sh
podman stop t && podman inspect t --format '{{.State.ExitCode}}'   # 0
```

`curl.sh` reports 8 failures without `MOCK_SMTP_API=true` in the container's
`config.env`. Those are the OTP-login checks, and a shipped image correctly
does not mock email delivery — set it only to get a clean 99/99 locally.
