#!/usr/bin/env sh
# Print the version the release artifacts should carry.
#
# One definition, because every job in release.yml needs the same answer and
# they feed it into fields with different grammars: RPM's `Version:`, Alpine's
# `pkgver`, `dotnet pack -p:Version`, and the artifact filenames that later
# jobs glob for. If two jobs disagree, a job downloads a tarball that is not
# there.
#
# Non-tag runs are the reason this exists. `${GITHUB_REF_NAME#v}` on a branch
# yields the BRANCH NAME, and a workflow_dispatch run then fed
# "supply-chain-verifiable-releases" into all of the above:
#
#   nuget   'supply-chain-verifiable-releases' is not a valid version string
#   rpm     error: line 5: Illegal char '-' (0x2d) in: Version: ...
#   apk     MSB4181 — build-apk.sh exports VERSION, and MSBuild reads the
#           environment as global properties, so `Version` became the branch
#           name and restore failed without logging a reason
#
# So release.yml could never be dispatched, only released from — which is
# exactly the workflow you least want to be unable to rehearse.
#
# The fallback is a plain 0.0.0 rather than something more descriptive like
# 0.0.0-dev.<sha>, because no richer string satisfies all four grammars at
# once: RPM forbids the '-', and Alpine's pkgver grammar
# ([0-9]+(\.[0-9]+)*[a-z]?(_(alpha|beta|pre|rc|cvs|svn|git|hg|p)[0-9]*)*)
# rejects the suffix. The build still stamps the real git description into the
# binary through InformationalVersion, so a test artifact can still say what it
# was built from; it just is not in the package version.
#
# Tag behaviour is unchanged: v1.2.6 -> 1.2.6.
set -eu

if [ "${GITHUB_REF_TYPE:-}" = "tag" ] && [ -n "${GITHUB_REF_NAME:-}" ]; then
	VERSION="${GITHUB_REF_NAME#v}"
else
	VERSION="0.0.0"
fi

# A version that is empty, or that carries a character these packagers reject,
# must fail here rather than three jobs later inside rpmbuild.
case "$VERSION" in
	''|*[!0-9A-Za-z.+_~-]*)
		echo "release-version.sh: refusing to emit '$VERSION'" >&2
		exit 1 ;;
esac

printf '%s\n' "$VERSION"
