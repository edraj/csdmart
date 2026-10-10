#!/usr/bin/env bash
# Runs a command while holding a lock on the local podman image store.
#
#   dist/podman-store-lock.sh pull IMAGE            # pull IMAGE unless present (shared)
#   dist/podman-store-lock.sh shared    -- CMD ...  # e.g. a `podman build`
#   dist/podman-store-lock.sh exclusive -- CMD ...  # e.g. `podman image prune -f`
#
# The self-hosted runners on a host share one service account, so one rootless
# store. A prune in one job while another job is pulling into that store
# deletes the half-written pull: until the pull has tagged it, the image is
# dangling as far as a prune can tell. The pull then fails with exit 125,
# "layer not known" or "image not known". That failed the Alpine APK job of
# the v1.6.1 and v1.6.4 releases, each time seconds after the RHEL 9 job's
# prune, while the APK job pulled the 1 GB SDK image.
#
# Pulls take the lock shared, so they still run side by side; a prune takes it
# exclusively, so it waits for every pull in flight and holds new ones off
# until it is done. Pull before `podman run` with `pull` rather than holding
# the lock for a whole build: the image is tagged once pulled, and a prune
# leaves a tagged image alone.
#
# The lock file sits beside the store, so every job using the store resolves
# the same one. With no podman or no flock (docker, macOS) the command runs
# unlocked. CONTAINER_ENGINE picks the engine for `pull`, as in the build
# scripts; PODMAN_STORE_LOCK overrides the lock file.
set -euo pipefail

usage() {
    echo "usage: $0 pull IMAGE | shared -- CMD... | exclusive -- CMD..." >&2
    exit 2
}

locked() { # locked -s|-x CMD...
    local flag=$1
    shift
    local lock=${PODMAN_STORE_LOCK:-}
    if [ -z "$lock" ] && command -v podman >/dev/null 2>&1 && command -v flock >/dev/null 2>&1; then
        local graphroot
        graphroot=$(podman info --format '{{.Store.GraphRoot}}' 2>/dev/null || true)
        [ -n "$graphroot" ] && lock="$(dirname "$graphroot")/dmart-store.lock"
    fi
    if [ -z "$lock" ] || ! command -v flock >/dev/null 2>&1; then
        "$@"
        return
    fi
    # A prune waits for the pulls in flight and a pull for a running prune;
    # an hour is far beyond either, so running out means something is stuck.
    local rc=0
    flock "$flag" --conflict-exit-code 75 -w "${PODMAN_STORE_LOCK_WAIT:-3600}" "$lock" "$@" || rc=$?
    if [ "$rc" -eq 75 ]; then
        echo "podman-store-lock: gave up waiting for $lock" >&2
    fi
    return "$rc"
}

[ $# -ge 2 ] || usage
mode=$1
shift
case "$mode" in
    pull)
        [ $# -eq 1 ] || usage
        engine=${CONTAINER_ENGINE:-podman}
        # Word-split on purpose: CONTAINER_ENGINE may carry options.
        # shellcheck disable=SC2086
        if $engine image inspect "$1" >/dev/null 2>&1; then
            exit 0
        fi
        # shellcheck disable=SC2086
        locked -s $engine pull "$1"
        ;;
    shared | exclusive)
        [ "$1" = "--" ] || usage
        shift
        [ $# -ge 1 ] || usage
        if [ "$mode" = shared ]; then locked -s "$@"; else locked -x "$@"; fi
        ;;
    *)
        usage
        ;;
esac
