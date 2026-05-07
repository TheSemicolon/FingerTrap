# Porta.Pty — vendor provenance

This directory contains a vendored copy of [tomlm/Porta.Pty](https://github.com/tomlm/Porta.Pty),
brought in to provide the macOS/Linux PTY backend without depending on its
NuGet package or pre-built binaries. See [ADR-0008](../../../adrs/0008-vendor-porta-pty.md)
for the decision rationale.

## Snapshot

| Field | Value |
|---|---|
| Upstream repo | https://github.com/tomlm/Porta.Pty |
| Vendored commit | `8185adf7d35a36d49ed09668f20b76c83986d3cc` |
| Vendored version | `1.0.7` |
| Vendor date | 2026-05-07 |
| License at vendor time | MIT (preserved verbatim in `LICENSE`) |

## Local patches

None initially. As we apply local fixes, document each here:

| Date | File | Purpose | Upstream PR (if any) |
|---|---|---|---|
| _none yet_ | | | |

The `Porta.Pty.csproj` here has been modified relative to upstream to drop
NuGet packaging metadata and to invoke the C-shim CMake build as part of
the .NET build. Those modifications are project-integration concerns, not
behavioural patches, and are not tracked in the table above.

## Sync procedure

When pulling a newer upstream release:

1. **Clone upstream at the new tag/commit** to a scratch location:
   ```bash
   git clone --depth 1 --branch <tag> https://github.com/tomlm/Porta.Pty /tmp/Porta.Pty.new
   ```
2. **Diff against our current vendored copy**, paying particular attention
   to any local patches recorded above:
   ```bash
   diff -ruN src-sidecar/external/Porta.Pty/Porta.Pty /tmp/Porta.Pty.new/src/Porta.Pty
   diff -ruN src-sidecar/external/Porta.Pty/Porta.Pty.Native /tmp/Porta.Pty.new/src/Porta.Pty.Native
   ```
3. **Re-audit** the C source (`Porta.Pty.Native/porta_pty.c`) and any new
   or substantially-changed C# files. Look for new dependencies, new
   `[DllImport]` declarations, new file-system or network calls.
4. **Verify the upstream license is still permissive** (MIT/BSD/Apache 2.0).
   If it changed to AGPL, BSL, proprietary, or another non-permissive
   license, **do not sync**. Stay on the vendored snapshot indefinitely or
   evaluate alternatives (see ADR-0008 escape hatches).
5. **Apply our local patches** on top of the new snapshot.
6. **Update `Porta.Pty.csproj`** if upstream added new package references
   (declare versions in `src-sidecar/Directory.Packages.props`, not in the
   csproj — we use Central Package Management).
7. **Run the smoke test and the sidecar test suite**:
   ```bash
   dotnet test src-sidecar
   python3 scripts/smoke-pty.py
   ```
8. **Update this `UPSTREAM.md`** with the new commit SHA, version, sync
   date, and any newly applied patches.

## Audit footprint

For reviewers (or future-you) auditing the vendored source:

| Area | Files | Lines |
|---|---|---|
| Native shim (C) | `Porta.Pty.Native/porta_pty.c` | ~275 |
| C# wrapper (cross-platform) | `Porta.Pty/*.cs` | ~370 |
| C# wrapper (Mac) | `Porta.Pty/Mac/*.cs` + `Porta.Pty/Unix/*.cs` | ~700 |
| C# wrapper (Linux) | `Porta.Pty/Linux/*.cs` + `Porta.Pty/Unix/*.cs` (shared) | ~440 |
| C# wrapper (Windows) | `Porta.Pty/Windows/*.cs` | ~820 |

The Windows path is vendored verbatim for forward-looking parity but is
not exercised by our M1/M2 builds (we run only on macOS/Linux).
`PlatformServices.cs` selects the appropriate provider at runtime via
`RuntimeInformation.IsOSPlatform`.
