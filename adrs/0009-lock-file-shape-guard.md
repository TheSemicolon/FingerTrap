# 0009 — Lock-file shape guard: RID-agnostic packages.lock.json enforced via scripts/check.sh

- Status: Proposed
- Date: 2026-05-07

## Context and problem statement

The sidecar projects use `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`
(set in `src-sidecar/Directory.Build.props`) to gate package integrity at
restore time. CI's `sidecar` job runs `dotnet restore --locked-mode`
**without `-r`** because the lock files are intentionally RID-agnostic —
the workflow comment in `.github/workflows/ci.yml` documents this design
choice.

A developer running `dotnet publish -r <rid> --self-contained
-p:PublishSingleFile=true` locally triggers an **implicit, RID-aware
restore** that mutates the working-tree lock files with `"net<tfm>/<rid>"`
graph entries (and, when single-file is enabled,
`Microsoft.NET.ILLink.Tasks` as a Direct entry from the trimmer). If the
mutated lock files are committed, every subsequent PR fails CI with
NU1004 across all three matrix platforms.

This trap fired in production: PR #15 merged with contaminated lock
files because branch protection didn't require status checks at the
time. PR #19 hotfixed the contamination; #20 tracked the longer-term
question of how to prevent recurrence.

The upstream behavior is structural and unfixed:

- **[NuGet/Home #8287](https://github.com/NuGet/Home/issues/8287)** —
  *"Lock files prevent publishing when `<RuntimeIdentifiers>` is set"*
  — open since 2019.
- **[dotnet/sdk #48795](https://github.com/dotnet/sdk/issues/48795)** —
  *"NU1004: `Microsoft.NET.ILLink.Tasks` version has changed"* — open,
  untriaged.
- **[dotnet/sdk #14819](https://github.com/dotnet/sdk/issues/14819)** —
  *"Forced locked mode doesn't work cross-platform"* — open.
- **[dotnet/aspnetcore #64897](https://github.com/dotnet/aspnetcore/issues/64897)**
  — *"locked-mode restore fails in Docker due to
  `Microsoft.AspNetCore.App.Internal.Assets`"* — filed Dec 2025, no team
  response.

There is no .NET 10 fix shipping or planned. Microsoft's own large
repos (`dotnet/runtime`, `dotnet/aspnetcore`) sidestep the problem by
not committing lock files at all.

## Considered options

- **Option A — `<RuntimeIdentifiers>` (plural) in csproj.** Bake the RID
  graph into the lock file at restore time so publish-side restore is
  a no-op. Rejected: structurally broken in .NET 10. NuGet#8287 confirms
  that adding RID-specific assets under `--locked-mode` fails with
  NU1004; the only thing this option changes is *which* step first
  contaminates the file, not whether contamination happens.
- **Option B — `<NuGetLockFilePath>` conditional on `$(RuntimeIdentifier)`.**
  Per-RID lock files (`packages.osx-arm64.lock.json`, etc.) so the
  publish-side restore writes to a separate, gitignored file.
  Rejected on supply-chain grounds: gitignored per-RID locks would be
  generated fresh on the CI runner and pass `--locked-mode` against
  themselves, defeating the integrity gate they're supposed to provide.
  No public .NET sample repo uses this pattern; no first-party
  validation that VS / `dotnet` tooling handles the conditional cleanly
  (NuGet#11149 reports historical bugs in this area).
- **Option C — pre-commit / CI shape guard.** A simple script that
  fails when any committed `packages.lock.json` contains
  `net<tfm>/<rid>/` keys. Cheap, surgical, doesn't try to fix the
  upstream NuGet bug.
- **Option D — Drop `<RestorePackagesWithLockFile>` entirely.** Match
  the pattern dotnet/runtime and dotnet/aspnetcore use. Rejected on
  supply-chain grounds: removes hash-based integrity verification for
  the whole transitive closure (including `StreamJsonRpc` →
  `Newtonsoft.Json`, historically a typosquat target, and Vanara's
  P/Invoke surface). This is a future ADR-0005-successor question, not
  in scope here.

## Decision outcome

Chosen option: **Option C — shape guard**, implemented as
`hooks/check-lock-shape.sh` and wired into `scripts/check.sh` (which
CI's `repo-check` job runs on every PR).

### Implementation

1. **`hooks/check-lock-shape.sh`** — POSIX-ERE grep against the
   contamination signature `"net[0-9]+\.[0-9]+/`. Returns 0 on clean,
   1 on contamination, 2 on environment failure. Two modes:
   - `--staged`: scans `git diff --cached` content (pre-commit
     semantics). For developers who symlink the script as
     `.git/hooks/pre-commit`.
   - default (working-tree): scans every committed-or-untracked
     `packages.lock.json` outside `bin/`/`obj/`. For CI integration and
     manual invocation.

2. **`scripts/check.sh`** — adds a `lock-shape` check section that
   invokes the guard and rolls its result into the script's overall
   pass/fail. Existing CI `repo-check` job runs `bash scripts/check.sh`
   already; no workflow file changes needed.

3. **`CONTRIBUTING.md`** — documents the publish workflow trap, the
   discipline ("don't commit publish-mutated lock files"), the safety
   net (the guard), and an optional install-as-pre-commit-hook
   instruction.

### Why this and nothing more

The guard's only invariant is unambiguous contamination: a clean,
RID-agnostic lock file has only `"net<tfm>"` (no slash) as TFM keys.
Any composite key is contamination. Adding more clever filters (e.g.,
keying on `Microsoft.NET.ILLink.Tasks` package name) introduces
false-positive risk: if we ever enable `<PublishTrimmed>true</...>`,
ILLink.Tasks legitimately belongs in the lock file, and a name-based
guard would silently strip a real entry. The shape-only check is
durable across future package-set changes.

No MSBuild defense-in-depth target was added. `scripts/check.sh` runs
in CI's `repo-check` job, which is a required status check on `dev` per
branch protection. A commit that bypasses the local pre-commit hook
(via `--no-verify`, or by not installing the hook) still trips the
guard at CI time before merge. An MSBuild-time guard would be redundant
with that gate.

### Consequences

- **Good:** Contaminated lock files cannot merge to `dev`. The
  recurrence path that bit PR #15 is closed.
- **Good:** Guard is a single small shell script; trivial to audit,
  test, and remove if a future ADR-0005-successor changes the
  lock-file strategy.
- **Good:** Guard does not bypass `--locked-mode` or hide real package
  changes — entries that survive the shape check are still
  hash-verified by NuGet.
- **Good:** CONTRIBUTING.md now exists as a starter for repo-specific
  newcomer onboarding (the `script-output-conventions`,
  `github-flow`, `conventional-commits`, and ADR rules were previously
  only in the agent-framework rules; new contributors had no in-repo
  documentation).
- **Bad:** Developers must remember not to commit publish-mutated
  lock files (or run a clean restore before staging). The optional
  pre-commit hook install reduces this friction but doesn't eliminate
  it.
- **Neutral:** The strategic question — should the project keep
  `<RestorePackagesWithLockFile>=true` long-term given that
  Microsoft's own large repos don't — is deferred to a future
  ADR-0005-successor. This ADR explicitly punts on that decision.

## References

- [NuGet/Home #8287](https://github.com/NuGet/Home/issues/8287) — `<RuntimeIdentifiers>` + lock-file design gap (open 2019)
- [dotnet/sdk #48795](https://github.com/dotnet/sdk/issues/48795) — `Microsoft.NET.ILLink.Tasks` lock-file mutation
- [dotnet/sdk #14819](https://github.com/dotnet/sdk/issues/14819) — locked-mode cross-platform divergence
- [dotnet/aspnetcore #64897](https://github.com/dotnet/aspnetcore/issues/64897) — Dec 2025 instance of the same class
- [.NET 7.0 breaking change: Automatic RuntimeIdentifier for publish](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/7.0/automatic-rid-publish-only) — the change that introduced the publish-side implicit RID restore
- ADR-0008 (vendor Porta.Pty) — the PR that surfaced the contamination
- Issue #20 (this ADR closes it)
- Issue #19 (hotfix that this ADR makes durable)
