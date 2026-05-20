# 0011 — SmolVM as the standard local verification substrate

- Status: Accepted
- Date: 2026-05-19

## Context and problem statement

`scripts/dev-setup.sh` exists so that a new contributor (or a maintainer
on a new machine) can go from `git clone` to a working build of all
three FingerTrap components — sidecar, UI, Tauri shell — without
chasing platform-specific prerequisites by hand. The script's
acceptance criterion (per #27) is that it reaches green from a single
`--install` invocation on a **fresh** Debian 13 / Ubuntu 24.04 host.

"Fresh" is the operative word. The contributor box where this script
is *authored* is never fresh — it already has `cc`, `dotnet`,
`cargo`, half-installed `nvm`, leftover apt sources, and a `$PATH`
shaped by years of unrelated work. Authoring without an isolated
verification substrate leads to the exact class of bug that surfaced
during #27:

- The script passed `--check` on the author's box.
- The script passed CI's `repo-check` job (which only lints, doesn't run).
- The script PASSed `--install` once edited to add `cargo-tauri` and
  `build-essential`.
- The script **failed** the first time it ran end-to-end on a fresh
  Debian 13 image, because `check_cargo_tauri` ran before
  `check_tauri_linux_deps` had installed the `build-essential`
  package that `cargo install tauri-cli`'s build script needed for
  `cc`. The script's author (the maintainer who wrote the new check)
  had `cc` on PATH and could not have observed this locally.

This problem is not unique to `dev-setup.sh`:

- `scripts/smoke-pty.py` exercises the sidecar against a real PTY.
  Running it against a clean Linux host (rather than the author's
  customised one) gives stronger signal about cross-distro PTY
  behaviour. The smoke test is currently hardcoded to
  `aarch64-apple-darwin` (line 24); a Linux re-run requires a Linux
  host, ideally a clean one.
- Reproducing user-reported `.deb` / `.AppImage` install failures
  requires a distro-clean host. Doing so on the maintainer's daily
  driver is destructive and unrepeatable.
- A hypothetical pre-PR self-check ("run the equivalent of CI
  locally before pushing") needs hermetic isolation to be meaningful.

GitHub Actions CI is the existing answer for the *push-side* of this
problem, but it has a >30-second feedback loop per iteration and
requires committing experimental changes. A **local** substrate would
shorten the iteration loop from minutes to seconds for the cases above.

The constraints on any candidate substrate:

- **Hermetic.** No leakage from the host's PATH, package state, dotfiles,
  or installed toolchains.
- **Fast cold start.** Slow enough to discourage use → not used → no value.
- **Cross-platform host support.** Maintainers run macOS and Linux today.
- **Real Linux kernel.** Required for verifying things like `patchelf`
  RPATH-rewriting of the sidecar ELF, GTK / WebKit loading inside the
  bundled `.deb` / `.AppImage`, and `cgroup`-mediated PTY behaviour.
- **OCI image input.** The same `debian:13` / `ubuntu:24.04` images CI
  uses, with no parallel image-build pipeline.
- **Trustable supply chain.** No anonymous binary blobs; the substrate
  itself should be auditable OSS.
- **Acceptable cost-of-ownership.** Single-binary install, no daemon,
  no system service, no separate VM-image build step.

## Considered options

- **Option A — SmolVM** ([smol-machines/smolvm](https://github.com/smol-machines/smolvm), Apache 2.0).
  Single-binary CLI. Each workload runs in its own microVM with hardware
  isolation via Hypervisor.framework (macOS) or KVM (Linux). VMM is
  [libkrun](https://github.com/containers/libkrun) with a custom kernel
  ([libkrunfw](https://github.com/smol-machines/libkrunfw)). OCI image
  input. Sub-second cold start. Network defaults off; egress allow-list
  via `--allow-host` / `--allow-cidr`. Stateful named machines
  (`machine create/start/stop/exec`) plus ephemeral mode. Empirically
  verified during #27: caught the `check_cargo_tauri` ordering bug on
  the first Debian 13 run.
- **Option B — Docker.** Ubiquitous, well-understood, large image
  ecosystem. Container, not a VM — shared kernel with the host, no
  hypervisor boundary. Daemon-shaped (rootful Docker requires the
  daemon; rootless Docker exists but is its own setup story). Adequate
  for the `dev-setup.sh` and `smoke-pty.py` cases. Inadequate for
  verifying `.deb` / `.AppImage` install behaviour against a real init
  + dynamic linker the way a VM exercises it (containers strip much of
  this).
- **Option C — Podman.** Daemonless Docker-compatible. Same
  container-not-VM caveats as Docker. Better trust posture (no daemon,
  rootless by default). On macOS, transparently runs a hidden QEMU VM
  to provide a Linux kernel — that VM is effectively a worse SmolVM.
- **Option D — Multipass** (Canonical). Real Ubuntu VMs, hypervisor
  isolation. Ubuntu-only image ecosystem (no first-class Debian images
  — would have to bring our own cloud-init recipe). Slower cold start
  than libkrun-based options. Daemon-shaped.
- **Option E — Lima.** Real Linux VMs on macOS via QEMU/Vz; on Linux
  defers to whatever local hypervisor is present. macOS-primary
  framing, less clean on a Linux host. Larger surface than we need.
- **Option F — Full QEMU + hand-rolled cloud-init.** Maximum control,
  maximum maintenance burden. Becomes its own subproject.
- **Option G — Do nothing.** Rely on CI as the only "fresh-environment"
  signal. Continue to discover ordering bugs after PR push, when the
  feedback loop is minutes per iteration and requires committing
  experimental changes.

## Decision outcome

Chosen option: **A — SmolVM**.

Why:

- **Empirical proof on the very case that motivated this ADR.** During
  #27, running the edited script in a SmolVM `debian:13` ephemeral
  machine surfaced an ordering bug (`cargo install tauri-cli` running
  before `build-essential` was installed) that the author could not
  have observed on their own box. The bug was fixed and re-verified
  in a second SmolVM run before the PR was opened. Net cost: ~10
  minutes of wall time; net benefit: avoided shipping a broken
  `--install` flow.
- **Right shape for the verification we actually need.** Real Linux
  kernel, real init, real dynamic linker — the things that matter for
  the `.deb` / `.AppImage` and `patchelf`-RPATH verification paths
  (#17, #24, follow-ups). Containers can do `dev-setup.sh` but cannot
  do the bundle-install paths fairly.
- **Cost-of-ownership is genuinely low.** One `curl ... | bash`
  installer to `~/.smolvm`, one `~/.local/bin/smolvm` binary, no
  daemon, no system service, no second package manager. OCI input
  reuses CI's existing image choices verbatim.
- **Trust posture is honest.** Apache 2.0; libkrun is upstream OSS
  ([containers/libkrun](https://github.com/containers/libkrun));
  libkrunfw is in the same org as SmolVM itself. The `curl | bash`
  installer is a real concern (see Bad below) but is no worse than
  `rustup` or `dotnet-install.sh`, both already accepted in this
  repo's `dev-setup.sh`.

Trade-offs explicitly accepted:

- **Linux maintainers need `/dev/kvm`.** Standard on Debian/Ubuntu;
  user must be in the `kvm` group. macOS maintainers need
  Hypervisor.framework, which ships in the OS.
- **No Windows host support.** Windows maintainers continue to rely on
  CI for fresh-environment verification, or use WSL2 (which provides
  KVM-via-Hyper-V) opportunistically. Not regressing today's posture.
- **Single-vendor dependency.** SmolVM is the only project with this
  exact shape. Mitigated by: the underlying libkrun + OCI standards
  are vendor-neutral; if SmolVM were ever abandoned, switching to
  podman-machine or lima for the same use cases is mechanical.
- **No CI replacement.** This ADR is explicitly about a *local*
  verification substrate. GitHub Actions remains the merge gate. We
  do not propose running SmolVM inside CI.

### Scope rules — when to use SmolVM

In order of expected use:

1. **MUST**: end-to-end verification of any change to
   `scripts/dev-setup.sh` on at least one Debian 13 and one Ubuntu 24.04
   ephemeral image, with the canonical recipe documented in
   `scripts/dev-setup.sh`'s header comment. PR description must include
   the SmolVM verification result.

2. **SHOULD**: any change to `scripts/smoke-pty.py`, the
   `BuildPortaPtyNative` MSBuild target, or anything that affects how
   `libporta_pty.so` is built / loaded on Linux. Run the smoke test
   inside a Linux SmolVM before opening the PR.

3. **SHOULD**: any change to Linux bundle behaviour (`.deb`,
   `.AppImage`, `patchelf` RPATH wiring in `.github/workflows/ci.yml`).
   Install the produced bundle inside a fresh `debian:13` /
   `ubuntu:24.04` SmolVM and confirm the sidecar launches and the PTY
   echoes a keystroke. This is the most-valuable use case — containers
   genuinely cannot verify this.

4. **MAY**: reproducing a user-reported bundle issue on the maintainer's
   host without contaminating the host.

5. **MAY**: pre-PR "run CI-equivalent checks locally" convenience
   workflow. Lower priority because CI itself is the merge gate.

6. **OUT OF SCOPE**: SmolVM as a CI runner replacement. SmolVM as a
   sandbox for untrusted agent / extension code. SmolVM-packed
   `.smolmachine` "dev box" distribution to new contributors (this is
   *interesting* but is implementation work tracked in #29, not an
   ADR-level decision).

### Consequences

- **Good.** Catches bugs of the class that motivated #27 at authoring
  time rather than at PR time. Establishes a single canonical
  fresh-environment recipe shared between `dev-setup.sh`, smoke
  testing, and bundle verification. Reduces dependence on the
  maintainer's daily-driver state for correctness signal. Composes
  cleanly with CI (does not replace it).
- **Bad.** Adds a substrate maintainers must install (`curl | bash`
  from `smolmachines.com`) to do their job in the cases where this ADR
  says MUST. Mitigated by: install is per-user, single-binary, no
  daemon, no system service, reversible by `rm -rf ~/.smolvm
  ~/.local/share/smolvm ~/.local/bin/smolvm`. The MUST scope is narrow
  enough (only `dev-setup.sh` changes) that maintainers who never
  touch that file are unaffected.
- **Bad.** Windows maintainers cannot satisfy the MUST clause directly.
  Workarounds: WSL2 with KVM enabled, or delegate fresh-environment
  verification to another maintainer / CI. Documented in the rollout
  (#29) rather than blocking adoption here.
- **Neutral.** The implementation work — recipe documentation,
  `.smolmachine` dev-box experiments, optional pre-PR wrapper script —
  is tracked separately in #29 and phased so individual pieces can
  land or be deferred independently.
- **Neutral.** Choosing SmolVM does not preclude later supplementing
  with podman or Docker for the cases where container semantics suffice.
  The ADR sets the *default* substrate; orthogonal tools remain
  available.

## References

- Issue #28 — evaluation request that produced this ADR.
- Issue #29 — implementation rollout (blocks on this ADR landing
  Accepted).
- Issue #27 — `dev-setup.sh` audit; the case study that surfaced
  SmolVM's value.
- PR #30 — first repo-internal application of SmolVM; commit `254e976`
  contains the canonical Debian 13 / Ubuntu 24.04 verification recipe
  embedded in `scripts/dev-setup.sh`'s header.
- [smol-machines/smolvm](https://github.com/smol-machines/smolvm) —
  upstream project (Apache 2.0).
- [containers/libkrun](https://github.com/containers/libkrun) —
  underlying VMM.
