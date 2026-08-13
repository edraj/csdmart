# Single-process Alpine container: dmart (AOT) on SQLite.
#
# Previous tags bundled PostgreSQL 18 and supervised two processes. This one
# ships dmart alone: the SQL store is a rebuildable index over the flat files,
# and for a single-node container the SQLite tier is the right size for it
# (see the readme's "Storage backends" section for where that tier stops).
# Point DATABASE_DRIVER / DATABASE_* at an external PostgreSQL for anything
# beyond that. The entrypoint refuses to start on a volume left behind by a
# PostgreSQL-era image rather than silently coming up with an empty index.
#
# Prerequisites: build UI first: ./build-ui.sh
#
# Usage:
#   podman build -t dmart -f admin_scripts/docker/Dockerfile .
#   podman run --name dmart -p 8000:8000 -d dmart

# NOT named VERSION. MSBuild promotes environment variables to properties, so a
# build arg called VERSION becomes $(Version) inside dotnet restore/publish —
# and the old default of "unknown" is not a parseable version, which made
# restore die with an unlogged "RestoreTask returned false" 16 ms in. The
# documented `podman build -f admin_scripts/docker/Dockerfile .` therefore
# never worked; CI only got away with it by always passing a real version.
ARG DMART_VERSION=0.0.0-local
# Git metadata is passed in from CI — .dockerignore excludes .git/ so the
# csproj's `git describe` fallback can't see anything here. GIT_DESCRIBE is
# the raw `git describe --tags --always` output (already encodes tag + SHA).
ARG GIT_DESCRIBE=
ARG GIT_BRANCH=unknown
ARG GIT_DATE=

# Stage 1: Build C# AOT binary (musl for Alpine)
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG DMART_VERSION
ARG GIT_DESCRIBE
ARG GIT_BRANCH
ARG GIT_DATE
RUN apk add --no-cache clang build-base zlib-dev
WORKDIR /src

# Restore NuGet packages in a cacheable layer of their own — only the
# project files are needed for restore, so unrelated source changes don't
# invalidate the cache. dmart.csproj ProjectReferences Dmart.Models and
# Dmart.QueryGrammar, so all three csproj files must be present when the
# restore graph is computed.
COPY dmart.csproj ./
COPY Dmart.Models/Dmart.Models.csproj Dmart.Models/
COPY Dmart.QueryGrammar/Dmart.QueryGrammar.csproj Dmart.QueryGrammar/
RUN dotnet restore dmart.csproj -r linux-musl-x64

# Bring in the full source and publish. .dockerignore filters out
# dmart.Tests/, cxb/ source, bin/, obj/, .git/, and other bulk that
# shouldn't inflate the build layer.
COPY . .
RUN INFO_DATE="${GIT_DATE:-$(date -u +%Y-%m-%d)}" && \
    INFO_VERSION="${GIT_DESCRIBE:-v${DMART_VERSION}}" && \
    dotnet publish dmart.csproj -r linux-musl-x64 --no-restore \
        -p:PublishAot=true -p:StripSymbols=true -c Release -o /out \
        -p:InformationalVersion="${INFO_VERSION} branch=${GIT_BRANCH} date=${INFO_DATE}" \
    && rm -f /out/*.dbg /out/*.pdb /out/*.Development.json \
             /out/*.staticwebassets* /out/*.deps.json

# Stage 2: Minimal runtime
FROM alpine:edge
ARG DMART_VERSION

# Install packages + copy all files in one layer.
# No icu-libs: dmart is built with InvariantGlobalization=true, so the binary
# has no ICU dependency (culture data is compiled out; all parses/compares are
# invariant/ordinal). tzdata STAYS — TimeZoneInfo reads OS tzdata, not ICU.
# No postgresql18: this image runs dmart on SQLite, whose engine ships inside
# the binary via SQLitePCLRaw. krb5-libs STAYS even though the PostgreSQL
# server is gone — Npgsql is still compiled in and this image documents
# pointing at an external PostgreSQL, so the client's Kerberos path must keep
# working. (DOTNET_SYSTEM_NET_SECURITY_DISABLEGSSAPIPAL below may make it
# redundant; that is a separate, testable trim, not something to infer.)
RUN apk add --no-cache \
      bash curl tzdata krb5-libs \
      jq

COPY --from=build --chmod=755 /out/dmart /usr/bin/dmart
# The SQLite engine reaches its native library through SQLitePCLRaw, which
# dlopens it — Native AOT cannot link it into the binary, so the publish output
# carries it as a sidecar and every packaging path has to place it. /usr/lib is
# on the default search path, matching where the RPM/deb/apk put it.
#
# Not optional here the way it is for those packages: this image runs on
# SQLite, so without it the process dies in SqliteConnection's static
# constructor before serving anything.
COPY --from=build --chmod=755 /out/libe_sqlite3.so /usr/lib/libe_sqlite3.so
COPY --from=build /out/plugins/ /usr/lib/dmart/plugins/
# Filesystem fallback copies of the embedded SPAs — musl-AOT doesn't always
# surface ManifestEmbeddedFileProvider resources, so the middleware falls
# back to these dirs at runtime (see CxbMiddleware / CatalogMiddleware).
COPY cxb/dist/client/     /usr/lib/dmart/cxb/
COPY catalog/dist/client/ /usr/lib/dmart/catalog/
COPY --chmod=755 admin_scripts/docker/entrypoint.sh /entrypoint.sh

ENV DOTNET_SYSTEM_NET_SECURITY_DISABLEGSSAPIPAL=true
LABEL name="dmart" vendor="dmart.cc" version="${DMART_VERSION}" license="AGPLv3+" \
      org.opencontainers.image.name="dmart" \
      org.opencontainers.image.vendor="dmart.cc" \
      org.opencontainers.image.version="${DMART_VERSION}" \
      org.opencontainers.image.description="dmart C# on SQLite — single-process Alpine container" \
      org.opencontainers.image.url="https://dmart.cc" \
      org.opencontainers.image.source="https://github.com/edraj/csdmart" \
      org.opencontainers.image.license="AGPLv3+"
EXPOSE 8000
CMD ["/entrypoint.sh"]
