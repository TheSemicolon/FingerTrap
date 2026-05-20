# Contributing to FingerTrap

Welcome. This document captures the repo-specific conventions you need to know before opening a PR. The architectural decisions live in [`adrs/`](adrs/) — read those for the *why*. This file covers the *how* of day-to-day work.

## Setup

Run the dev-environment check / install:

```bash
scripts/dev-setup.sh             # check what's missing
scripts/dev-setup.sh --install   # install missing deps (Debian/Ubuntu and macOS)
```

Required tools: .NET 10 SDK, Node 22+, pnpm 10, rustup + cargo, Tauri Linux deps (Linux only).

## Repo layout

| Path | Owner |
| --- | --- |
| `src-sidecar/` | .NET 10 sidecar — JSON-RPC over stdio |
| `src-sidecar/external/Porta.Pty/` | Vendored upstream PTY library — see [`ADR-0008`](adrs/0008-vendor-porta-pty.md) and [`UPSTREAM.md`](src-sidecar/external/Porta.Pty/UPSTREAM.md). **Do not edit upstream-style files here without updating UPSTREAM.md's local-patches table.** |
| `src-tauri/` | Rust shell — Tauri host + sidecar plumbing |
| `src-ui/` | Vite + xterm.js frontend |
| `adrs/` | Architecture Decision Records (sequential, no gaps; supersession not editing) |
| `scripts/` | Repo-wide tooling (`check.sh`, `dev-setup.sh`, smoke tests) |
| `hooks/` | Git hooks (opt-in install — see below) |

## Branching and PRs

- Feature/fix work targets `dev`, never `main`. `main` only receives `dev` via release-promotion PRs (per [`ADR-0004`](adrs/0004-repo-conventions.md) and the agent-framework `github-flow` rule).
- Feature branch naming: `<type>/kebab-case-description` where `<type>` is a Conventional Commits type (`feat`, `fix`, `docs`, `chore`, `refactor`, `test`, `ci`, `style`).
- PR title must be a valid Conventional Commits message — it becomes the squash-commit message on `dev`.
- PR template at [`.github/PULL_REQUEST_TEMPLATE.md`](.github/PULL_REQUEST_TEMPLATE.md) is mandatory; fill all sections.

## Sidecar publish workflow (read this!)

The sidecar has a **lock-file contamination trap** worth understanding before you publish locally.

### What you need to know

- Lock files (`packages.lock.json`) are committed and must be **RID-agnostic** — i.e., contain only `"net10.0"` as the TFM key, never `"net10.0/osx-arm64"` or any other RID composite.
- `dotnet publish -r <rid> --self-contained -p:PublishSingleFile=true` performs an implicit RID-aware restore that **rewrites your working-tree lock files with `"net10.0/<rid>"` graph entries**.
- If you commit the contaminated lock files, CI's `dotnet restore --locked-mode` (run without `-r` per the workflow design — see the comment in `.github/workflows/ci.yml`) fails with **NU1004 across all platforms**, blocking every subsequent PR until someone fixes it.

This is an upstream NuGet design gap ([NuGet#8287](https://github.com/NuGet/Home/issues/8287), open since 2019) — see [`ADR-0009`](adrs/0009-lock-file-shape-guard.md) for the analysis.

### The discipline

After running `dotnet publish` locally, **either**:

1. **Don't `git add`** the lock files. Just stage your source changes.
2. **Or regenerate clean** before staging:

   ```bash
   dotnet restore src-sidecar    # no -r, no --force-evaluate
   ```

   This produces RID-agnostic lock files matching what CI expects.

### The safety net

`scripts/check.sh` runs `hooks/check-lock-shape.sh` automatically and **fails CI's `repo-check` job** if any committed `packages.lock.json` contains a `net<tfm>/<rid>/` graph entry. Branch protection on `dev` requires `repo-check` to be green, so contamination cannot merge.

You can run the guard locally any time:

```bash
hooks/check-lock-shape.sh             # check working-tree
hooks/check-lock-shape.sh --staged    # check git-staged content (pre-commit semantics)
```

### Optional: install as a pre-commit hook

If you'd rather catch contamination at commit time instead of CI time, install the guard as your local `pre-commit` hook:

```bash
ln -s ../../hooks/check-lock-shape.sh .git/hooks/pre-commit
chmod +x hooks/check-lock-shape.sh
```

The hook auto-detects staged-vs-working mode by checking arguments; without args it scans the working tree, so the symlink does the right thing for pre-commit.

## Production bundle workflow

Day-to-day development uses `cargo tauri dev`, which reads
`src-tauri/binaries/libporta_pty.{dylib,so}` and stages it next to the
sidecar in `target/<profile>/` via `src-tauri/build.rs`. The dev workflow
is unchanged by the bundling work.

To produce a real `.app` / `.deb` / `.AppImage` locally, you must first
publish the sidecar so the companion native lib lands in
`src-tauri/binaries/` alongside the sidecar binary, then run the Tauri
bundler. See [`ADR-0010`](adrs/0010-tauri-bundle-companion-libs.md) for the
full mechanism (`bundle.macOS.frameworks` on macOS, `bundle.resources` +
RPATH on Linux).

### macOS (arm64)

```bash
publish_dir=$(mktemp -d)
dotnet publish src-sidecar/src/FingerTrap.Sidecar/FingerTrap.Sidecar.csproj \
  -c Release -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=embedded \
  -o "$publish_dir"
mkdir -p src-tauri/binaries
cp "$publish_dir/fingertrap-sidecar" \
   src-tauri/binaries/fingertrap-sidecar-aarch64-apple-darwin
cp "$publish_dir/libporta_pty.dylib" src-tauri/binaries/libporta_pty.dylib
cd src-tauri && cargo tauri build
# .app at src-tauri/target/release/bundle/macos/FingerTrap.app
```

After `dotnet publish`, **do not stage `packages.lock.json` changes** — the
publish-side restore rewrites them with RID-specific graph entries that
break CI. The lock-shape guard catches this if you forget. See the
"Sidecar publish workflow" section above.

### Linux (x64 or arm64)

```bash
publish_dir=$(mktemp -d)
dotnet publish src-sidecar/src/FingerTrap.Sidecar/FingerTrap.Sidecar.csproj \
  -c Release -r linux-arm64 --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=embedded \
  -o "$publish_dir"
mkdir -p src-tauri/binaries
sidecar_dst=src-tauri/binaries/fingertrap-sidecar-aarch64-unknown-linux-gnu
cp "$publish_dir/fingertrap-sidecar" "$sidecar_dst"
cp "$publish_dir/libporta_pty.so"   src-tauri/binaries/libporta_pty.so
patchelf --add-rpath '$ORIGIN/../lib/FingerTrap' "$sidecar_dst"
cd src-tauri && cargo tauri build
# .deb at src-tauri/target/release/bundle/deb/
# .AppImage at src-tauri/target/release/bundle/appimage/
```

The `patchelf` step is what tells the Linux dynamic linker to find
`libporta_pty.so` at `/usr/lib/FingerTrap/` (where `bundle.resources`
places it) when invoked from `/usr/bin/fingertrap-sidecar`.

### Skip bundling

To compile and link without producing a bundle (useful for fast
iteration):

```bash
cargo tauri build --no-bundle
```

## Validation before opening a PR

```bash
scripts/check.sh                      # repo structure + ADR numbering + lock-file shape
dotnet build src-sidecar -c Release   # 0 warnings, 0 errors
dotnet test  src-sidecar -c Release   # all green
python3 scripts/smoke-pty.py          # sidecar PTY end-to-end (macOS/Linux only)
cd src-ui && pnpm lint && pnpm build  # frontend
cd src-tauri && cargo fmt --check && cargo clippy --all-targets
```

CI runs the full matrix on Windows, macOS, and Linux automatically; the local checks above are the fastest signal.

## Verifying changes in a clean environment (SmolVM)

Some classes of change cannot be honestly verified on your daily-driver
box — it has too many leftover toolchains, dotfiles, and apt sources.
[ADR-0011](adrs/0011-smolvm-verification-substrate.md) adopts
[SmolVM](https://github.com/smol-machines/smolvm) as the standard
local fresh-environment substrate. The recipes below are the canonical
invocations referenced by that ADR.

### When SmolVM verification is required

- **MUST** — any change to `scripts/dev-setup.sh`. Include the result
  in the PR description.
- **SHOULD** — any change to `scripts/smoke-pty.py`, the
  `BuildPortaPtyNative` MSBuild target, or Linux bundle behaviour
  (`.deb`, `.AppImage`, `patchelf` RPATH wiring).
- **MAY** — reproducing a user-reported bundle issue without
  contaminating your host; pre-PR convenience runs.
- **Out of scope** — CI runner replacement, agent-sandbox use.

### One-time SmolVM install

```bash
curl -sSL https://smolmachines.com/install.sh | bash
export PATH="$HOME/.local/bin:$PATH"   # add to your shell rc
```

Linux: requires `/dev/kvm` and your user in the `kvm` group. macOS:
uses Hypervisor.framework (ships with the OS). Windows: not supported
— delegate fresh-environment verification to CI or use WSL2.

### Recipe 1 — verify `scripts/dev-setup.sh` end-to-end

Run the canonical Debian 13 + Ubuntu 24.04 pass. This is the MUST
recipe for any `dev-setup.sh` change.

```bash
# from the repo root
for image in debian:13 ubuntu:24.04; do
  echo "=== $image ==="
  smolvm machine run --net --image "$image" --cpus 6 --mem 6144 \
    -v "$PWD:/work:ro" -- bash -c '
      set -e
      apt-get update -qq
      DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
        sudo curl ca-certificates git xz-utils >/dev/null
      # Node precondition (dev-setup.sh prints guidance only;
      # see Risk note in PR #30).
      curl -sSL https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.1/install.sh | bash >/dev/null 2>&1
      . "$HOME/.nvm/nvm.sh" && nvm install 22 >/dev/null 2>&1
      bash /work/scripts/dev-setup.sh --install
    '
done
```

Expected: each image ends with `PASS — 0 errors, ...`. Wall time on a
modern x86_64 host is ~5–7 minutes per image (cargo install tauri-cli
is the bulk; compiles from source on first install). Paste the
`PASS` summary line for each image into the PR description.

### Recipe 2 — verify a `.deb` bundle installs cleanly

For Linux bundle changes (ADR-0010 territory). Produces a `.deb` on
the host via the [Production bundle workflow](#production-bundle-workflow),
then verifies it installs and the binary's dynamic-linker chain
resolves on a fresh Debian 13 / Ubuntu 24.04 image. Does **not**
launch the GUI — SmolVM has no display server. GUI launch is covered
by CI's `tauri` matrix and your own manual `cargo tauri build` run.

```bash
# Assumes you have already produced the .deb per CONTRIBUTING.md's
# "Production bundle workflow > Linux" section.
deb=$(ls src-tauri/target/release/bundle/deb/*.deb | head -1)
echo "Verifying: $deb"

for image in debian:13 ubuntu:24.04; do
  echo "=== $image ==="
  smolvm machine run --net --image "$image" -v "$PWD:/work:ro" -- bash -c '
    set -e
    apt-get update -qq
    # apt resolves the .debs declared dependencies (webkit2gtk, etc).
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "/work/'"$deb"'"
    bin=$(dpkg -L fingertrap | grep -E "/bin/fingertrap$" | head -1)
    echo "installed binary: $bin"
    # Verify the dynamic-linker chain resolves — in particular that
    # libporta_pty.so is found at /usr/lib/FingerTrap/ via the
    # RPATH set by patchelf in the publish step (ADR-0010).
    ldd "$bin" | grep -E "(libporta_pty|not found)" || true
    if ldd "$bin" | grep -q "not found"; then
      echo "FAIL: unresolved shared libraries"
      exit 1
    fi
    echo "PASS: all libraries resolved"
  '
done
```

Expected: `libporta_pty.so => /usr/lib/FingerTrap/libporta_pty.so` and
no `not found` entries on either image.

### Notes on coverage and follow-ups

- **smoke-pty.py is currently hardcoded** to `aarch64-apple-darwin`
  (line 24). Until it is RID-parameterized (tracked against #17), the
  SmolVM Linux runner recipe for the sidecar smoke test is deferred
  — see #29 item 2.
- **A prebuilt `FingerTrap.smolmachine` dev box** (image preloaded
  with .NET 10, Node 22, rust + cargo-tauri, Tauri Linux deps) would
  let new contributors skip the `dev-setup.sh` loop entirely. Build
  and distribution policy for that artifact is a separate scope —
  see #29 item 3.
- **A wrapper script** that runs `dev-setup.sh --check` + `check.sh`
  + the bundle-install recipe in one go would shorten the pre-PR
  loop. Optional polish — see #29 item 5.

## Architecture decisions

When making a change that introduces, modifies, or removes a convention, pattern, or technology choice, **add an ADR** under `adrs/`:

- Use [`adrs/TEMPLATE.md`](adrs/TEMPLATE.md) as the starting point.
- Numbering is sequential, zero-padded three digits, **never reused** (use supersession; do not edit a superseded ADR's body).
- `scripts/check.sh` validates ADR numbering and required-section presence.

## Commit messages

Conventional Commits, lowercase imperative, no trailing period. No `Co-authored-by:` trailers (per the agent-framework `conventional-commits` rule, which this project follows).

```text
fix(sidecar): bump Nerdbank.MessagePack to 1.1.62
^^^ ^^^^^^^   ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
type scope    description
```

## Issues and follow-ups

- File issues for bugs, enhancements, or scope you don't want to land in the current PR. Link them from the PR body.
- Latent / known-unfixed concerns belong in issues, not in code comments. Code comments rot; issues survive.
