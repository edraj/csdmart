# Recorded dependency graph

One file per project: the full NuGet graph — direct and transitive, with
content hashes — as resolved by the SDK named in `../LOCKFILE_SDK`.

These are `packages.lock.json` documents kept deliberately **outside** the
build. MSBuild never reads them, and `packages.lock.json` is gitignored, because
a committed lock file breaks this repository's builders: distro .NET
installations resolve the SDK-injected `ILCompiler`/`ILLink.Tasks` packages from
a local `library-packs` folder whose content hashes differ from nuget.org's
(NU1403), and the versions of those packages follow the SDK, which differs
across our build environments (NU1004).

Regenerate and check with `../check-dependency-graph.sh`. It runs on every pull
request, and in both release workflows before any SBOM is generated — so an
SBOM on a release describes a dependency set that was reviewed here.
