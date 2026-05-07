# 0008 — Vendor Porta.Pty: replace direct libc P/Invoke with a non-variadic C shim

- Status: Proposed
- Date: 2026-05-07

## Context and problem statement

ADR-0007 chose direct libc P/Invoke for the macOS PTY backend, mirroring the
Linux pattern from ADR-0006. End-to-end validation on Apple Silicon (M2 Max,
macOS 26.4 SDK) surfaced a fundamental incompatibility that ADR-0007 did not
anticipate: **`.NET 10` cannot reliably P/Invoke into libc's variadic
functions on Apple Silicon.** Specifically, `ioctl(int, unsigned long, ...)`
returns `rc=0` without taking effect on the kernel.

The failure mode is silent. We confirmed it by adding a `TIOCSWINSZ` set
followed immediately by a `TIOCGWINSZ` readback — the readback returns
`cols=0 rows=0` despite the set having returned success. We tried four
distinct P/Invoke patterns:

1. `LibraryImport` source-generator with `ref WinSize` — set returns 0,
   readback returns zeros.
2. `LibraryImport` with `[Out] byte[]` — same: set returns 0, buffer not
   filled. (This was the original failure for `TIOCPTYGNAME`, sidestepped
   by switching to `ptsname_r` which is non-variadic.)
3. `LibraryImport` with raw `nint` pointer — same.
4. Legacy `DllImport` with `WinSize*` — same.
5. Raw `delegate* unmanaged[Cdecl]<...>` via `NativeLibrary.GetExport`,
   bypassing all P/Invoke marshaling — same.

A pure-C reproducer compiled with clang against the same SDK and called
exactly the same syscall pattern (parent opens master, child opens slave
via `posix_spawn` file actions, parent ioctl on master) returns the
correct values. The bug is not in the kernel, the magic numbers, the
struct layout, or our PTY initialisation order. It is in the .NET runtime's
inability to generate an Apple ARM64 PCS-correct call frame for variadic
libc functions. Reference: dotnet/runtime#48752 (open).

ADR-0007's design — direct managed P/Invoke to `ioctl` — is therefore not
viable on macOS arm64 with .NET 10. We need a different approach for the
PTY backend.

The PTY service shape decisions in ADR-0006 (sessionId, base64 framing,
debounced resize, RPC method names) are unaffected and remain in force.
Only the implementation strategy for the backend changes.

## Considered options

- **Write our own non-variadic C shim** — partially attempted in this branch
  (`src-sidecar/native/macos/pty_shim.c`). Eliminates the variadic problem
  but adds a custom native dependency we maintain alone, with no pre-built
  CI machinery, no Linux/Windows path, and no test coverage. Two days of
  build-system plumbing to reach where Porta.Pty already is.
- **Use `portable-pty` (Rust crate) and move PTY into `src-tauri`** —
  used in production by WezTerm. Eliminates the .NET sidecar's PTY layer
  entirely; PTY runs in Rust where the variadic issue does not exist.
  Largest refactor: re-shape the entire PTY data path through Tauri
  commands/channels instead of JSON-RPC over stdio. Defers the M1 closure
  by perhaps a week of refactor work. ADR-0006's decisions about JSON-RPC
  framing and sidecar surface would need partial revisitation.
- **Use `node-pty` via a Node sidecar** — most popular cross-platform
  PTY library overall (VS Code, Hyper). Replaces the .NET sidecar with
  a Node sidecar. Same magnitude of refactor as the Rust option, plus
  introduces Node as a runtime dependency.
- **Use `Pty.Net` from NuGet** — already evaluated and rejected in
  ADR-0006 §1: the published `Pty.Net` 0.1.6-pre is Windows-only. Now
  also unlisted on nuget.org and abandoned upstream (last meaningful
  commit November 2021).
- **Vendor `Porta.Pty` source into our repo** (this ADR). Porta.Pty is
  the spiritual successor to `Pty.Net`, MIT-licensed, and ships a C shim
  that calls `ioctl` from native code — the exact pattern that solves
  our problem. Vendoring (rather than taking a NuGet dependency) means
  we own the build pipeline and the binaries we ship; supply-chain trust
  in the upstream maintainer is bounded to the snapshot we audit.

## Decision outcome

Chosen option: **vendor `Porta.Pty` 1.0.7 source into
`src-sidecar/external/Porta.Pty/` and build the native C shim as part of
our own CI**.

### Why vendor rather than take a NuGet dependency

`Porta.Pty` is a single-maintainer project, no NuGet ID-prefix reservation,
and ships pre-built `.dylib`/`.so` binaries with no public reproducible CI.
For a PTY library that has full read/write access to the user's interactive
shell — including credential prompts, sudo passwords, ssh sessions —
delegating native-binary trust to a single upstream maintainer is a
material risk increment over delegating C# logic.

Vendoring source + building binaries in our CI means:

- The native binaries that ship in our app are built on public GitHub
  Actions runners from public source, not on someone's laptop.
- The C source (~275 lines) is small enough to audit fully.
- The C# wrapper (~2230 lines across all platforms) is in our tree and
  reviewable in PRs.
- Upstream license changes do not affect our snapshot.
- Upstream availability does not affect our build.

### Vendor layout

```text
src-sidecar/external/Porta.Pty/
├── LICENSE                              # MIT, copied verbatim
├── UPSTREAM.md                          # Provenance metadata (see below)
├── README.md                            # From upstream, unmodified
├── src/
│   ├── Porta.Pty/Porta.Pty.csproj       # Modified: drop NuGet pack, build C shim
│   ├── Porta.Pty/<all C# files>         # Verbatim from upstream
│   └── Porta.Pty.Native/
│       ├── porta_pty.c                  # Verbatim from upstream
│       └── CMakeLists.txt               # Verbatim from upstream
```

`FingerTrap.Sidecar.csproj` adds a `<ProjectReference>` to
`external/Porta.Pty/src/Porta.Pty/Porta.Pty.csproj`. The C shim builds as a
pre-compile MSBuild target invoking `cmake` for the current
`$(RuntimeIdentifier)`. The compiled `libporta_pty.{dylib,so}` ships next
to the sidecar binary in the publish output and is loaded by .NET at
runtime via `DllImport("libporta_pty")`.

`MacOsPtyService.cs`, `MacOsNativeMethods.cs`, `LinuxPtyService.cs`, and
`LinuxNativeMethods.cs` are deleted. A single `PtyService.cs` adapts
Porta.Pty's `IPtyConnection` to our `IPtyService` interface. Platform
branching now lives inside Porta.Pty, not our code.

### Provenance metadata (`UPSTREAM.md`)

Every vendored copy carries a metadata file recording:

- Upstream repo URL: <https://github.com/tomlm/Porta.Pty>
- Vendored commit SHA: `8185adf7d35a36d49ed09668f20b76c83986d3cc`
- Vendored version: `1.0.7`
- Vendor date: `2026-05-07`
- License at vendor time: `MIT`
- Local patches: (none initially; tracked in this file going forward)
- Sync procedure: documented inline (see `UPSTREAM.md` for steps)

### Escape hatches (the contingency plans)

Vendoring decouples us from upstream after the fact. The following
escape hatches are available and most are baked in by the act of
vendoring itself:

1. **License snapshot.** The MIT terms at vendor time grant perpetual
   rights to the snapshot we vendored. Upstream cannot retroactively
   relicense. If upstream relicenses to AGPL or proprietary going
   forward, our snapshot is unaffected; we just cannot accept new
   updates without re-evaluating.

2. **Build self-sufficiency.** Our CI builds the C shim from source via
   CMake. Zero runtime dependency on upstream's NuGet feed, GitHub
   Packages, or release artifacts. If upstream's repo is deleted
   tomorrow, our build is unaffected.

3. **Architectural abstraction.** `IPtyService` in
   `FingerTrap.Sidecar.Abstractions` already isolates the PTY backend
   from the rest of the sidecar. Porta.Pty becomes one implementation
   behind that interface. Swapping to `portable-pty` (Rust), a different
   .NET library, or a from-scratch implementation is "rewrite one
   adapter file" not "rewrite the sidecar."

4. **Patch in place.** If upstream gets a CVE we cannot wait on, or we
   need a feature, we edit the vendored source directly. Tracked in
   `UPSTREAM.md` patches list. We can forward to upstream as a PR if the
   license permits, but we don't have to.

5. **Promote to fork-and-package.** If we want to own a NuGet feed for
   this code (e.g., to consume from multiple projects later), we can
   push our `external/Porta.Pty/` to a new repo, add NuGet pack to its
   CI, replace `<ProjectReference>` with `<PackageReference>`. ~2-3
   hours. Always possible because the snapshot's MIT license permits it
   regardless of what upstream does after.

6. **Greenfield as backstop.** If both upstream and our vendor become
   untenable somehow, the absolute floor is reimplementing from scratch.
   The C shim is ~100 lines per platform; the C# wrapper is ~500 lines
   we'd actually use (Mac + Linux + Unix base; Windows can stay deferred
   per ADR-0006). ~1-2 weeks if we had to. We are never blocked, just
   inconvenienced.

### What is NOT mitigatable

- **License at vendor time being incompatible.** This is the kill-switch.
  Verified at vendor: Porta.Pty 1.0.7 is MIT.
- **Pre-existing CVE in the source we vendor.** Same as for any
  dependency. Mitigated by initial audit and ongoing source review.

### Consequences

- Good: macOS arm64 PTY now works (the variadic ABI issue is fully
  bypassed by routing through a non-variadic C shim).
- Good: massive code reduction in our sidecar — `MacOsPtyService` (576
  lines), `MacOsNativeMethods` (126), and the Linux equivalents are all
  deleted. Net reduction: ~1500 lines of platform-specific P/Invoke,
  replaced with a ~100-line `PtyService` adapter.
- Good: Linux and macOS share one implementation path (Porta.Pty handles
  the platform branching internally). The duplication concern raised in
  ADR-0007 §"code structure" is moot.
- Good: when Windows lands, no separate ConPty backend needed in our
  code — Porta.Pty's Windows path comes for free.
- Good: ADR-0006 §1 "PTY backend" is clarified by this ADR. The
  "libc P/Invoke" decision is refined to "P/Invoke through a
  non-variadic C shim" — same approach, just one indirection that fixes
  the arm64 variadic problem.
- Good: every escape hatch above is structurally available, not
  hypothetical.
- Bad: We take on a vendored dependency we did not have before. Updates
  require manual sync (estimated <1 hour per sync; PTY semantics are
  stable).
- Bad: Build now requires `cmake` and a C compiler on every developer
  machine and CI runner. macOS dev machines have these via Xcode CLT
  already; Linux runners need `apt install cmake build-essential`; CI
  workflow will be updated.
- Bad: One-time audit cost (already paid in this branch).
- Neutral: Supersedes ADR-0007. The macOS-specific constants table and
  spawn-handle ABI notes in 0007 remain useful historical record but no
  longer reflect the live implementation.

## Supersedes

- [0007 — macOS PTY implementation](0007-macos-pty-implementation.md)
  (status updated to Superseded in that ADR's Status line; body
  unchanged per the "Supersession, not editing" rule).

## Refines

- [0006 — PTY service shape](0006-pty-service-shape.md) §1 "PTY backend":
  the libc P/Invoke decision is refined to "P/Invoke through a vendored
  non-variadic C shim". Other decisions in 0006 (sessionId, base64,
  debounce, RPC surface, Linux-first scope) are unchanged.
