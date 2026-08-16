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
# THIS IMAGE INSTALLS THE ALPINE PACKAGE; it does not compile dmart.
#
# It used to build the binary itself, in an `mcr.microsoft.com/dotnet/sdk:
# 10.0-alpine` stage — compiling linux-musl-x64 a second time, because the
# Alpine APK job already produces exactly that binary. On the release runners
# that duplicate AOT compile cost ~5 minutes of a shared 3-runner pool.
#
# Installing the .apk instead means the image ships the SAME artifact an Alpine
# user installs, so the container also serves as a test of the package. The APK
# carries everything this image needs — binary, libe_sqlite3.so, plugins, and
# the cxb/catalog SPAs — so nothing is copied in alongside it.
#
# Prerequisites:
#   ./build-ui.sh                          # the APK bakes the SPAs in
#   VERSION=<v> ./dist/build-apk.sh --arch x86_64
#
# Usage:
#   podman build -t dmart -f admin_scripts/docker/Dockerfile .
#   podman run --name dmart -p 8000:8000 -d dmart

ARG DMART_VERSION=0.0.0-local

# Pinned, and pinned to MATCH the build side: dist/build-apk.sh compiles inside
# mcr.microsoft.com/dotnet/sdk:10.0-alpine, which is Alpine 3.24 with musl
# 1.2.6. Running that binary on the same Alpine release is the only way to know
# the musl it was linked against is the musl it gets.
#
# This was `alpine:edge`, with no recorded reason — edge is a rolling
# PRE-RELEASE (3.25.0_alpha at the time of writing), so the published image's
# base could change under it between builds and its musl could drift ahead of
# the one the binary was built against.
FROM alpine:3.24

ARG DMART_VERSION

# Runtime dependencies, then the package itself.
#
# No icu-libs: dmart is built with InvariantGlobalization=true, so the binary
# has no ICU dependency (culture data is compiled out; all parses/compares are
# invariant/ordinal). tzdata STAYS — TimeZoneInfo reads OS tzdata, not ICU.
# No postgresql18: this image runs dmart on SQLite, whose engine ships inside
# the binary via SQLitePCLRaw. krb5-libs STAYS even though the PostgreSQL
# server is gone — Npgsql is still compiled in and this image documents
# pointing at an external PostgreSQL, so the client's Kerberos path must keep
# working. (DOTNET_SYSTEM_NET_SECURITY_DISABLEGSSAPIPAL below may make it
# redundant; that is a separate, testable trim, not something to infer.)
RUN apk add --no-cache bash curl tzdata krb5-libs jq

# --allow-untrusted: the APK is signed with an ephemeral key generated inside
# the build container (see dist/build-apk.sh), so there is no published key to
# verify against. The package is not fetched from a repository — it is the
# artifact this same release just produced, sitting in the build context.
COPY dist/out/dmart-*-x86_64.apk /tmp/dmart.apk
RUN apk add --no-cache --allow-untrusted /tmp/dmart.apk \
    && rm -f /tmp/dmart.apk \
    # The package seeds /etc/dmart/config.env for a host install. This image
    # does not use it: entrypoint.sh generates /root/.dmart/config.env and
    # points BACKEND_ENV at that. Leaving both would give an operator two
    # config files and one of them no effect, so the unused one goes.
    && rm -f /etc/dmart/config.env

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
