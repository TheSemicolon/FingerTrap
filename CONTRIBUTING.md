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
