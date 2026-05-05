# Milestones

## M0 — Skeleton

Three-process scaffold. JSON-RPC `ping` round-trips between TS UI and .NET
sidecar through the Tauri shell. CI green on Windows, macOS ARM64, and
Linux. Initial ADRs (0001–0005) land.

**Acceptance:** `pnpm tauri dev` opens a window. The Rust shell spawns
`fingertrap-sidecar` as a child process. The TS UI calls `api.ping("hello")`
from a button click. The sidecar's `RpcSurface.PingAsync` returns
`"pong: hello"`. The reply renders in the window. CI is green on all three
runners. `scripts/check.sh` passes locally.

## M1 — Local PTY integration (Linux first)

First real keypress end-to-end. `IPtyService` implementation spawns a
local shell over a pseudoterminal. xterm.js renders sidecar PTY output
via JSON-RPC notifications (`pty/output`). Keystrokes flow back through
`pty/write`. Resize is debounced on the .NET side and forwarded via
`pty/resize`. Sidecar emits `pty/exit` when the shell terminates.

The Linux backend is implemented via direct libc P/Invoke
(`posix_openpt` + `posix_spawn` with `POSIX_SPAWN_SETSID`); see
ADR-0006. macOS and Windows are deferred — `pty/spawn` throws
`PlatformNotSupportedException` on those platforms until their
backends land.

**Acceptance (Linux):** `pnpm tauri dev` opens a window with a shell
prompt rendered in xterm.js. `ls` produces correct output. Keystrokes
echo. Window resize updates the PTY size and the prompt redraws
cleanly. CI is green on all three runners (sidecar/ui/tauri matrices
build everywhere; PTY behavior is exercised on Linux only).

## M2 — Local terminal panes

Multiple PTY sessions. Split panes (horizontal/vertical). Focus management.
Per-pane shell selection. Pane lifecycle (create, focus, close) flows
through `RpcSurface`.

## M3 — SSH terminal

SSH.NET integration. `ISshService` owns SSH sessions and exposes an internal
accessor that M4's SFTP service reuses — no second auth path. Connection
profiles persisted (encrypted at rest using OS keychain). Remote PTY
through xterm.js.

## M4 — SFTP tree

SFTP file browser sharing the SSH connection from M3. Tree view in left
rail. File download and upload. Drag-and-drop into the active terminal pane.

## M5 — Status providers

`IStatusProvider` plumbing in the sidecar. Adding a new source is one class
plus one registration — no plumbing changes. Initial providers:

- Git status (LibGit2Sharp)
- GitHub PRs (Octokit)
- Azure DevOps work items

Status bar renders providers dynamically. Providers raise events; the bar
re-renders.

## M6 — Command palette and keymap

Global keymap. Command palette (Cmd/Ctrl+P). Configurable bindings via
settings. Commands invoke RPC methods or trigger UI actions.

## M7 — Settings and persistence

User config (theme, font, default shell, profiles). Persisted layout
(panes, sizes, focus). Settings UI. Settings stored in
`<app-data>/fingertrap/settings.json` with versioned schema.

## M8 — Packaging

`tauri build` with signed installers:

- macOS: hardened runtime, notarization, JIT entitlement (or NativeAOT
  switch — see ADR-0005 successor)
- Windows: code signing (EV or OV)
- Linux: `.deb` and AppImage

Auto-update channel via Tauri updater. Sidecar publish revisited per
ADR-0005 successor (trimming/AOT migration). Release automation via
`semantic-release` on `main`.
