# Verifying a csdmart release

Every csdmart release is checksummed, signed, and accompanied by build
provenance. This document tells you how to check all of it, what each check
proves, and — the part that matters most for a security review — **what none of
it proves.**

The whole verification is one script:

```sh
./scripts/verify-release.sh --tag v1.2.6
```

It exits `0` only if every asset on the release is checksummed, signed by one of
this repository's two release workflows, and carries build provenance naming the
tagged commit. It exits non-zero, with a per-check explanation, otherwise.

---

## 1. The runbook block

Drop this into a deployment runbook. It fails closed: nothing is installed
unless verification passed.

```sh
#!/usr/bin/env bash
set -euo pipefail

VERSION=v1.2.6                      # the release you intend to deploy
REPO=edraj/csdmart
WORK=$(mktemp -d)

# 1. Verification tooling. Both are single static binaries.
#    cosign: https://github.com/sigstore/cosign/releases
#    gh:     https://github.com/cli/cli/releases   (>= 2.49)
command -v cosign >/dev/null && command -v gh >/dev/null

# 2. Fetch the verifier FROM THE TAG YOU ARE DEPLOYING, not from main.
git clone --depth 1 --branch "$VERSION" "https://github.com/$REPO" "$WORK/src"

# 3. Verify. Non-zero exit here must abort the deployment.
"$WORK/src/scripts/verify-release.sh" --tag "$VERSION" --repo "$REPO" \
    --dir "$WORK/assets" --keep

# 4. Only now install. For RHEL 9:
sudo dnf install -y "$WORK/assets/dmart-${VERSION#v}-1.el9.x86_64.rpm"

# Or the plain binary tarball:
tar -xzf "$WORK/assets/dmart-${VERSION#v}-linux-x64.tar.gz" -C /opt
```

Useful flags:

| Flag | Effect |
|---|---|
| `--require-hosted` | Fail any artifact built on a self-hosted runner. See §5. |
| `--commit <sha>` | Also require the provenance to name exactly this commit. |
| `--offline` | Verify attestations from the bundled `.jsonl` assets rather than GitHub's API. |
| `--repo <owner>/<repo>` | Verify a fork's release (identity is pinned to that repo). |
| `--keep` | Keep the downloaded assets after a successful run. |

---

## 2. What is on a release, and what each file is

| Asset | What it is |
|---|---|
| `dmart-<v>-linux-x64.tar.gz`, `dmart-<v>-linux-arm64.tar.gz` | The Native AOT server: `dmart`, `libe_sqlite3.so`, `plugins/`, `config.env.sample`, `LICENSE`, `BUILDINFO`. Built inside AlmaLinux 9, so the glibc floor is 2.34 — they run on RHEL 9 and newer. |
| `dmart-<v>-1.el9.x86_64.rpm`, `…fc44…rpm`, `…src.rpm` | RPM packages (RHEL/AlmaLinux 9, Fedora, and the source RPM). |
| `dmart_<v>_amd64.deb` | Debian/Ubuntu package. |
| `dmart-<v>-x86_64.apk`, `dmart-<v>-aarch64.apk` | Alpine (musl) packages. |
| `dmart-<v>-win-x64.zip`, `dmart-<v>-osx-arm64.zip` | Windows and macOS builds. |
| `cxb-<v>.tar.gz`, `catalog-<v>.tar.gz` | The two SPA bundles, for serving from a web root. (They are also embedded in the binary.) |
| `Dmart.*.nupkg` / `.snupkg` | The client/model SDK packages, same versions as on nuget.org. |
| `<asset>.sig` | Detached cosign signature over that asset. |
| `<asset>.pem` | The short-lived Fulcio certificate the signature was made with. Its SubjectAlternativeName is the workflow that signed. |
| `<asset>.cdx.json` | CycloneDX SBOM for that artifact's NuGet dependency graph. |
| `<asset>.attestation.jsonl` | The SLSA provenance bundle, attached so verification can run without calling GitHub's API. |
| `SHA256SUMS` | Checksums of the linux tarballs and their signature material, written by `release-verifiable.yml` at tag time. |
| `SHA256SUMS-all` | Checksums of **every** asset on the release, written by `release.yml` after packaging finished. |
| `SHA256SUMS*.sig`, `SHA256SUMS*.pem` | Signatures over those two manifests. |

There is no `.sig`-less asset. An unsigned file on a csdmart release is a
finding, and `verify-release.sh` treats it as a failure rather than skipping it.

**Not covered:** the container image at `ghcr.io/edraj/csdmart` is neither signed
nor attested. If you deploy the image rather than a package, you are outside
everything this document describes.

---

## 3. What each check proves

**Checksums** (`sha256sum -c SHA256SUMS`, `SHA256SUMS-all`) prove the bytes you
downloaded are the bytes the manifest names. On their own they prove nothing
about origin — the manifest could have been written by anyone. They matter
because the manifest itself is signed.

**cosign signature** (`cosign verify-blob`) proves that a GitHub Actions job,
running `release-verifiable.yml` or `release.yml` **in this repository, at this
tag**, held the bytes and asked Sigstore's Fulcio CA to certify a signature over
them. There is no long-lived key anywhere: the job exchanges its OIDC token for
a certificate valid for ten minutes, signs, and the certificate plus signature
are recorded in Rekor, Sigstore's public append-only transparency log. The
verifier pins:

- the certificate's SAN, to `https://github.com/edraj/csdmart/.github/workflows/{release,release-verifiable}.yml@refs/tags/<tag>`;
- the OIDC issuer, to `https://token.actions.githubusercontent.com`;
- the certificate's GitHub extensions, to this repository and this tag ref.

A signature made by any other workflow, repository, tag, or issuer fails.

**Build provenance** (`gh attestation verify`) is a SLSA v1 provenance statement
binding the artifact's SHA-256 digest to the workflow, the run, the commit, and
the runner environment that produced it. `verify-release.sh` additionally pins
`--source-ref` to `refs/tags/<tag>`, so provenance from a branch build cannot
satisfy a tag verification.

**SBOM attestation** binds a CycloneDX document to the same artifact digest. The
SBOM lists the NuGet dependency graph — direct and transitive — resolved by the
actual `dotnet restore` for that target, read from `project.assets.json`.

The graph the SBOM reports is itself recorded in git. `dist/deps/` holds the
full resolved graph — direct and transitive, with content hashes — for every
project, and the job that generates the SBOMs first runs
`dist/check-dependency-graph.sh` on a pristine checkout with the SDK pinned to
`dist/LOCKFILE_SDK`. If the recorded graph and a fresh restore disagree, the
release fails before an SBOM is produced. So an SBOM on a release describes a
dependency set that was reviewed in a pull request, not whatever NuGet happened
to resolve that minute.

One honest detail about the SBOMs: today they are **identical across targets**
apart from metadata. Every `PackageReference` in this project is RID-agnostic,
and the parts that genuinely differ per target (the ILCompiler and runtime
packs) are build-toolchain components that CycloneDX does not record. They are
still generated and attested per target rather than once, so that the day a
RID-conditional dependency is added it appears in the SBOM for the targets it
affects instead of being averaged away.

---

## 4. What a failure means

| What you see | What it means | What to do |
|---|---|---|
| `checksum mismatch` | The bytes on disk are not the bytes the signed manifest names. Corrupt download, a proxy rewriting content, or substitution. | Re-download once. If it recurs, stop and report it. |
| `no signature material (.sig/.pem missing)` | An asset is on the release that no workflow signed. Either something was uploaded by hand, or a release job failed after uploading but before signing. | Do not deploy. Ask the maintainers whether the asset is theirs. |
| `cosign verify-blob FAILED` … `none of the expected identities matched` | The signature is valid but was made by a different workflow, repo, or tag than the one you are verifying. This is what a re-signed or copied artifact looks like. | Do not deploy. This is the serious one. |
| `cosign verify-blob FAILED` … signature verification error | The asset does not match its signature — it was modified after signing. | Do not deploy. |
| `build provenance verification FAILED` … `HTTP 404` | No attestation exists for these bytes. The artifact was not produced by an attested workflow run — or was modified since. | Do not deploy. |
| `build provenance verification FAILED` … source ref mismatch | The artifact was built, but not from this tag. | Do not deploy; a release built from an unexpected ref needs an explanation. |
| `missing required asset matching …` | The release is incomplete. | Do not deploy a partial release; the run may still be in progress. |
| `was built on a SELF-HOSTED runner` (a note, not a failure) | Expected for the distro packages. See §5. | Decide per your own policy; `--require-hosted` turns it into a failure. |

---

## 5. What this does **not** prove

Read this section before quoting any of the above to an auditor.

**It is not a reproducible build.** Nobody, including us, can rebuild these
binaries and get byte-identical output. Native AOT compilation is not
deterministic across toolchain and environment differences, and we make no
attempt to claim otherwise. The build sets `ContinuousIntegrationBuild`,
`Deterministic` and `PublishRepositoryUrl`, and the tarballs are packed with
normalised timestamps and ownership — that removes *some* incidental variation,
but it does not make the binary reproducible, and you cannot independently
recreate it to compare. **Provenance is an attestation about a build, not a
proof about a binary.** It says "GitHub ran this workflow at this commit and
these bytes came out", which you either believe or do not.

**You are trusting GitHub, in several distinct ways.**

1. *The identity itself.* Fulcio issues the signing certificate because GitHub's
   OIDC service asserts "this token belongs to workflow X at ref Y". Sigstore
   does not independently check that claim. Anyone who can make GitHub issue
   such a token — GitHub itself, or an attacker inside it — can produce
   signatures this document calls valid.
2. *Who can trigger a signing run.* Anyone who can push a workflow change and a
   `v*` tag to this repository can mint a genuine, verifying signature over
   whatever they built. Verification proves the artifact came from our CI. It
   does not prove it came from an honest change. Branch protection, review
   policy and tag permissions on the repo are load-bearing parts of this chain,
   and they live entirely inside GitHub.
3. *Attestation storage and lookup.* `gh attestation verify` fetches
   attestations from GitHub's API by default. `--offline` with the bundled
   `.jsonl` assets avoids that, at the cost of trusting the bundle you were
   given alongside the artifact.
4. *The runner.* The build and the attestation happen in the same job on the
   same machine. That is SLSA Build **L2**, not L3: there is no isolated,
   separately-trusted builder. A compromised step earlier in the job could hand
   different bytes to the attestation step, and the resulting provenance would
   verify perfectly.

**Rekor is the one anchor outside GitHub** — and it moves the trust rather than
removing it. The transparency log is operated by Sigstore, and verifying an
inclusion proof means trusting Sigstore's TUF root. The value it adds is
detection, not prevention: a forged signature that was logged is discoverable
after the fact, and one that was not logged fails verification.

**The distro packages are built on our own machines.** The RPM, `.deb`, APK,
NuGet and SPA jobs run on self-hosted runners; only the two linux tarballs, the
SBOMs and the checksum manifests are built on GitHub-hosted runners. Their
provenance says so honestly (`runnerEnvironment: self-hosted`), and
`verify-release.sh` prints it for each artifact. For those artifacts the claim
is "built by our CI on infrastructure we operate", which is weaker than "built
on infrastructure neither we nor an attacker can quietly modify". If your policy
requires the stronger claim, deploy the linux tarball and run
`verify-release.sh --require-hosted`.

**The dependency graph is recorded, not enforced at build time — and the
toolchain is neither.** `dist/deps/` fixes what a review saw; it is checked
against a fresh restore in CI and before every SBOM, on one pinned SDK. It is
not a `packages.lock.json`, and the builders do not consume it. They cannot: a
committed lock file fails NU1403 on any distro .NET installation, because those
resolve the SDK-injected ILCompiler/ILLink.Tasks from a local `library-packs`
folder whose content hashes differ from nuget.org's, and NU1004 across SDKs,
because those packages' versions follow the SDK. So a builder could in
principle resolve something the recorded graph does not name; what the check
guarantees is that no such difference existed at the pinned SDK, on the tagged
commit, in the same run.

The compiler is not pinned either. `global.json` sets a floor (a
10.0.1xx-or-later release SDK, never a preview, never .NET 11) rather than an
exact version, because the builders disagree: AlmaLinux 9 ships 10.0.111, the
`mcr.microsoft.com/dotnet/sdk:10.0` images behind the `.deb` and the APKs ship
10.0.400. Two artifacts in the same release are therefore compiled by different
SDK patch levels. Their *shipped* dependency graphs are identical — the
differing packages are build-time tooling that never enters the SBOM — but if
your threat model includes the compiler, this is the seam.

**Tags move; commits do not.** A tag can be deleted and re-pointed at a
different commit. Provenance records the commit, so pin *that*:

```sh
# Read the commit out of the provenance, review it, then require it explicitly.
gh attestation verify dmart-1.2.6-linux-x64.tar.gz --repo edraj/csdmart \
  --format json --jq '.[].verificationResult.statement.predicate.buildDefinition.resolvedDependencies'
./scripts/verify-release.sh --tag v1.2.6 --commit <that-sha>
```

**The SBOM is a NuGet dependency graph, nothing more.** It does not list the
.NET SDK, the ILCompiler, clang, or the system libraries the binary links
against (glibc, OpenSSL, zlib). It is generated from the restore graph, not
extracted from the compiled binary, so it describes what the build resolved
rather than what the linker ultimately kept.

**None of this says the software is safe.** It establishes origin. Whether the
source at that commit is trustworthy is a separate question, and the answer is
not in a signature.

---

## 6. Verifying by hand

If you would rather not run our script — a reasonable instinct for a script
whose job is to tell you our artifacts are fine:

```sh
TAG=v1.2.6
REPO=edraj/csdmart
FILE=dmart-1.2.6-linux-x64.tar.gz
ISSUER=https://token.actions.githubusercontent.com

# Checksums
sha256sum -c SHA256SUMS

# Signature: identity pinned to an exact workflow, repo, tag and issuer.
cosign verify-blob \
  --certificate      "$FILE.pem" \
  --signature        "$FILE.sig" \
  --certificate-identity "https://github.com/$REPO/.github/workflows/release-verifiable.yml@refs/tags/$TAG" \
  --certificate-oidc-issuer "$ISSUER" \
  --certificate-github-workflow-repository "$REPO" \
  --certificate-github-workflow-ref "refs/tags/$TAG" \
  "$FILE"

# Provenance
gh attestation verify "$FILE" --repo "$REPO" \
  --cert-identity "https://github.com/$REPO/.github/workflows/release-verifiable.yml@refs/tags/$TAG" \
  --cert-oidc-issuer "$ISSUER" \
  --source-ref "refs/tags/$TAG"

# SBOM attestation
gh attestation verify "$FILE" --repo "$REPO" \
  --predicate-type https://cyclonedx.org/bom \
  --cert-identity "https://github.com/$REPO/.github/workflows/release-verifiable.yml@refs/tags/$TAG" \
  --cert-oidc-issuer "$ISSUER"
```

The distro packages are signed by `release.yml` rather than
`release-verifiable.yml` — substitute the filename in `--cert-identity`.

### Air-gapped

Copy the release assets in, then:

```sh
./scripts/verify-release.sh --tag v1.2.6 --dir ./assets --no-download --offline
```

`--offline` verifies attestations from each `<asset>.attestation.jsonl` instead
of calling GitHub's API. Sigstore's trusted root is still needed for the
certificate chain; supply one with `gh attestation verify
--custom-trusted-root` if the host has no network at all.

**Signature verification is not fully offline.** `cosign verify-blob` checks the
Rekor entry for a detached `.sig`/`.pem` pair, which means reaching the
transparency log. A Sigstore bundle would remove that, but cosign v3 writes the
bundle *instead of* the detached pair rather than alongside it, and producing
both would mean signing twice and logging the same artifact to Rekor twice. We
chose the detached pair, because it is what verifies on cosign v2 as well as
v3, and downstream operators do not all run the same major. If a genuinely
air-gapped signature check matters to you, say so — it is a change we can make,
not a limitation of the design.

---

## 7. For maintainers: where each artifact comes from

| Workflow | Trigger | Runner | Produces |
|---|---|---|---|
| `release-verifiable.yml` | `push` of a `v*` tag | GitHub-hosted (`ubuntu-24.04`, `ubuntu-24.04-arm`) | linux-x64 / linux-arm64 tarballs, their SBOMs, `SHA256SUMS`; waits for the release to be created, then uploads |
| `release.yml` | `release: created` | mostly self-hosted | RPMs, `.deb`, APKs, Windows/macOS zips, SPA tarballs, NuGet packages, `SHA256SUMS-all`, container image |

Both first run `dist/check-dependency-graph.sh` on a clean checkout with the
SDK from `dist/LOCKFILE_SDK`, so a release cannot be built from a dependency
graph that is not the one recorded in git.

Both sign and attest **inside the job that built the artifact**, never in a
central job afterwards — see the comment at the top of
`.github/actions/sign-attest/action.yml` for why that placement is deliberate.

The tag-push workflow deliberately does **not** create the GitHub release. A
release created with `GITHUB_TOKEN` does not fire `release: created`, which
would silently stop every package in `release.yml` from being built. Cut a
release the same way as always; the tag-push run waits up to 30 minutes for it,
then uploads.
