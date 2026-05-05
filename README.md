# FingerTrap

Personal terminal application — local and SSH terminals, SFTP file tree, status surfaces, command palette.

## Architecture

Three processes, one app:

1. **Tauri shell** (`src-tauri/`) — Rust. Window, WebView, sidecar lifecycle, stdio bridge. No business logic.
2. **UI** (`src-ui/`) — TypeScript + Vite + xterm.js. Terminal panes, SFTP tree, status bar, command palette.
3. **Sidecar** (`src-sidecar/`) — .NET 10. Pty.Net, SSH.NET, LibGit2Sharp, Octokit, Azure DevOps SDK. Owns 95% of the logic.

IPC is JSON-RPC 2.0 over stdio with `Content-Length` framing — `StreamJsonRpc` on the .NET side, `vscode-jsonrpc` on the TS side.

## Repo layout

```text
adrs/        Architecture Decision Records (MADR minimal)
docs/        Project documentation (milestones, design notes)
scripts/     Repo-level scripts (check.sh, etc.)
src-sidecar/ .NET 10 sidecar (FingerTrap.sln)
src-tauri/   Tauri 2 shell (Rust crate)
src-ui/      TypeScript + Vite UI
```

## Conventions

- **Branching:** GitHub Flow. Feature branches into `dev` (squash-merge); `dev` into `main` (merge commit). `<type>/kebab-case` branch names.
- **Commits:** Conventional Commits. `<type>(<scope>): <description>`, imperative lowercase, no period.
- **Versioning:** SemVer tags from `main` only, annotated, `v` prefix. Pre-1.0 breaking changes bump MINOR.
- **ADRs:** Sequential zero-padded three digits in `adrs/`. Supersession over editing.

See `adrs/0004-repo-conventions.md` for the canonical version.

## Local development

Verify and install required tooling:

```bash
scripts/dev-setup.sh             # check only
scripts/dev-setup.sh --install   # install missing tools (rustup, pnpm via corepack, apt deps)
```

Tools required: .NET 10 SDK, Node.js 22+, pnpm 10 (via corepack), rustup
(stable) + cargo, and on Linux the Tauri system libraries. After installing
rustup, run `. "$HOME/.cargo/env"` (or restart your shell) before invoking
`cargo`.

Per-component dev loops:

```bash
# Sidecar (.NET)
cd src-sidecar && dotnet restore && dotnet build && dotnet test

# UI (TypeScript)
cd src-ui && pnpm install && pnpm lint && pnpm typecheck && pnpm build

# Tauri (Rust) — requires rustup + Linux system deps on Linux
cd src-tauri && cargo fmt --check && cargo clippy -- -D warnings && cargo check

# Repo-level
scripts/check.sh
```

## Status

M1 — local PTY (Linux). See `docs/milestones.md` for the full M0–M8 sequence.
