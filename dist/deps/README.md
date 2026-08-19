# Recorded dependency graph

One file per project: the NuGet graph — direct and transitive, with content
hashes — as resolved from the tracked source tree.

These are `packages.lock.json` documents kept deliberately **outside** the
build. MSBuild never reads them, and `packages.lock.json` is gitignored,
because a committed lock file breaks this repository's builders: distro .NET
installations resolve the SDK-injected `ILCompiler`/`ILLink.Tasks` from a local
`library-packs` folder whose content hashes differ from nuget.org's (NU1403),
and those packages' versions follow the SDK, which differs across our build
environments (NU1004).

Two consequences of how they are produced, both worth knowing before reading a
diff:

- **The SDK's own packages are stripped.** `Microsoft.DotNet.ILCompiler`,
  `Microsoft.NET.ILLink.Tasks` and the per-RID ILCompiler runtime packs are
  build tooling: they arrive with the SDK, never ship, and never appear in the
  SBOM. They are also the only entries whose bytes depend on where the SDK came
  from. Removing them is what makes this record identical on every machine.
- **The restore runs in a pristine copy of the tracked tree**, so a leftover
  `obj/` cannot change what gets recorded.

Regenerate and check with `../check-dependency-graph.sh`. It runs on every pull
request (`dependency graph in sync`) and in both release workflows before any
SBOM is generated — so an SBOM on a release describes a dependency set that was
reviewed here.
