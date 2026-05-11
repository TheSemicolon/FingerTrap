# 0010 — Package companion native libraries into the Tauri production bundle

- Status: Accepted
- Date: 2026-05-11

## Context and problem statement

The .NET sidecar consumes Porta.Pty (ADR-0008), which calls into a companion
native library `libporta_pty.{dylib,so}` via `[DllImport("libporta_pty")]`.
The library is required at runtime — without it, `pty/spawn` throws
`Unable to load shared library 'libporta_pty'`.

For `cargo tauri dev`, `src-tauri/build.rs` (added in PR #15) copies the
library next to the sidecar in `target/<profile>/`. This worked for dev only.

For `cargo tauri build` (production), the library was **not** packaged into
the resulting `.app` / `.dmg` / `.deb` / `.AppImage` / `.rpm`. Tauri's
`externalBin` config only handles the sidecar executable itself; companion
files are out of scope. Releases would ship a broken sidecar — `dlopen`
failing on the first `pty/spawn` call.

The acceptance criteria (issue #16) are:

- `cargo tauri build` on macOS arm64 produces a `.app` whose sidecar can
  spawn a PTY without DYLD failing to load `libporta_pty.dylib`.
- Same for `cargo tauri build` on Linux x64/arm64 producing a `.deb` and
  `.AppImage`.

## How .NET resolves DllImport at runtime (the load-bearing constraint)

A `[DllImport("libporta_pty")]` in a managed assembly does NOT generate an
`LC_LOAD_DYLIB` load command on the Mach-O image. The .NET runtime calls
`NativeLibrary.Load("libporta_pty")` lazily on first use, which in turn
calls `dlopen` against a series of candidate paths. The runtime's resolver
probes (empirically verified against `.NET 10` on macOS by the dyld error
the user reproduced on first install):

1. `<app-dir>/libporta_pty.dylib`
2. Bare `libporta_pty.dylib`
3. `<app-dir>/liblibporta_pty.dylib` (with the conventional `lib` prefix)
4. Bare `liblibporta_pty.dylib`
5. Variants without the `.dylib` extension
6. Standard `dlopen` fallback (`DYLD_LIBRARY_PATH`, `/usr/lib`, dyld cache)

`<app-dir>` resolves to the directory of the executing apphost binary
(`Contents/MacOS/` inside a `.app` bundle).

This matters because dyld's `LC_ID_DYLIB` install_name resolution — `@loader_path`,
`@executable_path`, `@rpath` — is consulted **only** when an image has an
`LC_LOAD_DYLIB` load command referencing the dylib. A .NET single-file app
does not. The dylib's install_name is metadata for static linkers; it plays
no role in the runtime `dlopen` path the .NET resolver takes.

**Conclusion:** the dylib must live in `Contents/MacOS/` adjacent to the
sidecar. Putting it in `Contents/Frameworks/` (which would be the
canonical Apple location) makes the dylib invisible to .NET's resolver.

The same shape applies on Linux: .NET probes the apphost directory first,
then falls back to `dlopen("libporta_pty.so")` which uses the OS dynamic
linker's standard search. The OS linker DOES consult the binary's `DT_RPATH`,
which is patchable via `patchelf` — so on Linux, the placement can diverge
from the binary's directory as long as RPATH covers it.

## Considered options

- **`bundle.macOS.files` (macOS) + `bundle.resources` map (Linux).** Place
  the dylib in `Contents/MacOS/` directly next to the sidecar so .NET's
  app-directory probe finds it. On Linux, the `.so` goes to
  `/usr/lib/<productname>/` via `bundle.resources` and `patchelf` adds an
  RPATH `$ORIGIN/../lib/<productname>` to the sidecar so the OS dynamic
  linker can resolve `dlopen("libporta_pty.so")` to the resource path. The
  install_name of the dylib stays at the conventional `@loader_path/...` (it
  is metadata, irrelevant to runtime resolution here, but kept consistent).
  Trade-off: `bundle.macOS.files` entries are NOT added to Tauri's
  `sign_paths` collection, so the dylib will not be auto-signed when
  notarization is wired. A future `beforeBundleCommand` step will need to
  invoke `codesign` on `src-tauri/binaries/libporta_pty.dylib` before
  Tauri seals the outer `.app`.

- **`bundle.macOS.frameworks` (macOS).** The Apple-canonical location for
  a companion dylib is `Contents/Frameworks/`. Tauri's bundler signs
  entries from this list automatically. **Rejected because .NET's runtime
  resolver does not probe `Contents/Frameworks/`** — a dylib placed there
  is invisible to `NativeLibrary.Load`, regardless of how the install_name
  is set. Empirically verified: the first attempt at this PR used
  `bundle.macOS.frameworks` with `install_name @executable_path/../Frameworks/...`,
  produced a `.app` with the dylib correctly placed in `Contents/Frameworks/`,
  signed metadata aside; the installed app failed at runtime with eight
  `dlopen` probe failures, all targeting `Contents/MacOS/`.

  We could work around this by either (a) overriding `.NET`'s
  `NativeLibrary.SetDllImportResolver` in the sidecar to look in
  `../Frameworks/`, or (b) emitting a launcher shim that sets
  `DYLD_LIBRARY_PATH=...Frameworks/`. (a) adds bundle-layout coupling to
  managed code and requires patching vendored Porta.Pty to register the
  resolver before the first DllImport. (b) is fragile and not the canonical
  Apple pattern for a notarized hardened-runtime app. Neither pays for
  itself relative to `bundle.macOS.files`.

- **Custom `beforeBundleCommand` script copying into the bundle staging
  directory.** Runs before the bundler reads its manifest; files copied
  this way are not seen by the bundler. Useful only as a fail-fast
  validation gate. Used in this design for that purpose.

- **`build.rs` extension to handle the bundle phase.** Runs at `cargo build`
  time, before the bundler. Cannot know the bundle staging path.

- **Tauri plugin pattern.** No plugin in the Tauri 2 workspace addresses
  companion-file bundling.

## Decision outcome

Chosen option: **`bundle.macOS.files` + `bundle.resources` map** with a
fail-fast validation gate via `beforeBundleCommand`.

### Mechanism

1. **macOS (`tauri.macos.conf.json`):** declare the dylib via
   `bundle.macOS.files` with destination key `MacOS/libporta_pty.dylib`
   (relative to `Contents/`) and value `binaries/libporta_pty.dylib`
   (source, relative to `src-tauri/`). The bundler copies the file to
   `Contents/MacOS/libporta_pty.dylib`, adjacent to the sidecar.

   The dylib's install_name remains `@loader_path/libporta_pty.dylib` —
   the conventional Apple value. It is not consulted at runtime for our
   load path, but staying conventional keeps the dylib useable by any
   future consumer that DOES link against it via `LC_LOAD_DYLIB`.

2. **Linux (`tauri.linux.conf.json`):** declare the `.so` via
   `bundle.resources` map form with key `binaries/libporta_pty.so` (source)
   and value `libporta_pty.so` (destination relative to the resource
   directory). The bundler places it at `/usr/lib/<productname>/libporta_pty.so`
   in `.deb`, equivalent paths in `.AppImage` / `.rpm`. Since the sidecar
   lives in `/usr/bin/` and the directories are not adjacent, **apply
   `patchelf --add-rpath '$ORIGIN/../lib/FingerTrap'` to the staged
   sidecar binary** in CI so the OS dynamic linker resolves
   `dlopen("libporta_pty.so", ...)` to the resource directory.

3. **Windows:** no companion file. Porta.Pty's Windows path uses ConPTY
   via Vanara P/Invoke to inbox `kernel32.dll`. The C-shim build target is
   already guarded to macOS / Linux only.

4. **`scripts/stage-native-lib.sh` as a fail-fast gate:** invoked via
   `build.beforeBundleCommand`. Verifies that
   `src-tauri/binaries/libporta_pty.{dylib,so}` exists before the bundler
   runs. A missing file becomes a loud pre-bundle failure rather than a
   confusing post-bundle runtime crash. Windows short-circuits with
   success.

5. **CI staging in `.github/workflows/ci.yml`:** after `dotnet publish`,
   copy `libporta_pty.{dylib,so}` from the publish output to
   `src-tauri/binaries/` alongside the sidecar. Apply `patchelf` on Linux.

### Codesign deferral

`bundle.macOS.files` entries are not added to Tauri's automatic codesign
sweep. The dylib in `Contents/MacOS/libporta_pty.dylib` will be unsigned
when the bundle is sealed.

For local installs today this is fine — Gatekeeper does not block unsigned
dylibs in unsigned apps. When notarization is wired in a future
milestone, a `beforeBundleCommand` extension (or a new `beforeSignCommand`
if Tauri 2 grows one) must invoke `codesign` on the staged dylib **before**
Tauri seals the outer `.app`. The dylib's signature has to be inside the
outer app's signature for nested-bundle notarization to pass.

This is a tractable problem deferred to the codesign-identity milestone,
not a structural flaw in this design.

### Why not move PTY to Rust to sidestep all of this

Considered in ADR-0008's "options" section (`portable-pty` in Rust host).
The trade-off there was a multi-week refactor of the PTY data path and a
re-shaping of ADR-0006's RPC surface. Worth keeping as a long-horizon
option but not what closes issue #16 today.

## Consequences

- Good: production `.app` / `.deb` / `.AppImage` / `.rpm` packages are
  shippable; the sidecar can spawn a PTY on first run.
- Good: dev mode (`cargo tauri dev`) is unaffected — `build.rs` continues
  to stage the dylib into `target/<profile>/`, and .NET's app-directory
  probe finds it there.
- Good: the bundling decision is declarative in two small platform overlay
  files; future companion files for other native deps follow the same
  pattern.
- Good: no patches to vendored Porta.Pty source. Upstream sync stays
  trivial.
- Bad: the dylib is not auto-signed by Tauri's bundler on macOS. When
  notarization is wired, a manual `codesign` step on the staged dylib must
  be added before the outer `.app` is sealed.
- Bad: Linux requires the `patchelf` step in CI staging — one more moving
  part. Mitigated by `patchelf` already being installed in the Linux
  dependency step of the `tauri` job.
- Bad: Windows local development cannot run `tauri build` without a bash
  shell on PATH (the `beforeBundleCommand` is bash). Deferred concern;
  M3+ when Windows bundling matters.
- Neutral: `bundle.active` flips from `false` to `true`. `cargo tauri
  build` now produces real bundles by default; use
  `cargo tauri build --no-bundle` to suppress.

## Refines

- [0008 — Vendor Porta.Pty](0008-vendor-porta-pty.md) — unchanged. ADR-0008
  remains authoritative for the vendoring decision and the C-shim build
  target. This ADR sits downstream of 0008 and addresses only the bundle-
  packaging mechanism; the dylib's install_name decision in 0008 is also
  unchanged (we keep `@loader_path/libporta_pty.dylib`).
