# 0006 — PTY service shape: libc P/Invoke, session IDs, base64 byte framing, debounced resize

- Status: Accepted
- Date: 2026-05-04

## Context and problem statement

M1 introduces the first real terminal session: keystrokes flow from xterm.js
through the Tauri shell to the .NET sidecar, into a child process attached
to a pseudoterminal, and PTY output flows back the same path. The IPC
contract is JSON-RPC over stdio (ADR-0002); the surface pairing rule
(ADR-0003) requires a typed facade in `src-ui/src/api.ts` for every method
in `RpcSurface.cs`.

Several shape decisions affect every subsequent milestone (M2 panes, M3
SSH, M5 status providers that may stream events) and are easier to settle
once than churn later:

1. Which PTY backend does the sidecar use?
2. Are sessions identified, or implicit?
3. How are raw PTY bytes framed inside JSON-RPC?
4. How is resize handled, given ConPTY's well-known race conditions?
5. Which platforms must work for M1 acceptance?

## Considered options

### 1. PTY backend

- **`Pty.Net` from NuGet** — initially planned, but the published package
  (`Pty.Net` 0.1.16-pre, the only line on nuget.org) is Windows-only:
  ConPty + WinPty backends, no Linux or macOS support. Rejected as the
  cross-platform option it claims to be.
- **libc P/Invoke (this ADR)** — `posix_openpt` + `posix_spawn` with
  `POSIX_SPAWN_SETSID` from a small managed wrapper. Avoids
  fork-from-managed-code hazards that plague `forkpty` in multi-threaded
  runtimes. Works on both Linux and macOS with the same approach (only
  numeric constants differ). Windows requires a separate ConPty backend
  (likely re-introducing Pty.Net or porting its ConPty layer).
- **External helper binary** — compile a small C helper that opens a PTY
  and exec's the shell, communicate via additional pipes. Adds a build
  step and a per-RID artifact for marginal benefit. Rejected.

### 2. Session identification

- **Implicit single session for M1** — simpler now; rename every method
  signature when M2 adds panes. Visible-everywhere churn.
- **Explicit `sessionId` from M1 (this ADR)** — every PTY method takes a
  string session ID. M1 creates one ID at app start and reuses it; M2 just
  adds more callers.

### 3. Byte framing inside JSON-RPC

- **UTF-8 string** — readable in logs, but raw PTY output is not always
  valid UTF-8 (multibyte sequences can split at chunk boundaries; programs
  emit binary). Round-tripping bytes through a string field requires
  agreed-upon escaping or replacement.
- **base64-encoded bytes (this ADR)** — every payload survives as exact
  bytes. ~33% wire overhead vs. raw UTF-8. JSON-safe, no escaping
  ambiguity, no encoding negotiation. The chunking is preserved exactly so
  xterm.js sees the same byte stream a real terminal would.
- **Binary RPC frame alongside JSON-RPC** — out of scope for M1; would
  break ADR-0002.

### 4. Resize handling

- **Forward every resize immediately** — on Linux/macOS this is fine via
  `ioctl(TIOCSWINSZ)`; ConPty on Windows is racy under rapid resize and
  can deadlock or drop output. Deferring the issue to the Windows
  milestone is fine, but the sidecar shape should already be ready.
- **Debounce on the .NET side (this ADR)** — sidecar coalesces resize
  requests over a short window (50 ms) and applies the latest. Smooths
  drag-resize on every platform; works around ConPty without UI logic
  when Windows lands.

### 5. M1 platform scope

- **All three OSes for M1 acceptance** — original M1 plan. ConPty work is
  the long pole; macOS signing and code-signed Tauri builds add friction
  without product value at this stage.
- **Linux first, macOS next, Windows last (this ADR)** — Linux is the
  primary daily-driver target for the maintainer. M1 acceptance only
  requires Linux end-to-end. CI continues to build the sidecar on all
  three OSes (drift detection); end-to-end behavior on macOS is M1-stretch
  and Windows is deferred to a later milestone.

## Decision outcome

Chosen options:

1. **libc P/Invoke** for the local PTY backend on Linux, with the same
   approach planned for macOS. Windows gets its own ConPty backend later.
2. **Explicit `sessionId`** on every PTY method from M1.
3. **base64-encoded bytes** for `pty/write` payloads and `pty/output`
   notification data.
4. **Debounced resize** (50 ms) inside the PTY service on the .NET side.
5. **Linux-first M1 acceptance**; macOS next, Windows deferred. The
   sidecar throws `PlatformNotSupportedException` for `pty/spawn` on
   non-Linux platforms until those backends land.

The RPC surface added in M1:

| Method            | Kind         | Params                                       | Returns         |
|-------------------|--------------|----------------------------------------------|-----------------|
| `pty/spawn`       | Request      | `{ sessionId, shell?, cwd?, cols, rows, env? }` | `{ pid }`    |
| `pty/write`       | Request      | `{ sessionId, dataBase64 }`                  | `void`          |
| `pty/resize`      | Request      | `{ sessionId, cols, rows }`                  | `void`          |
| `pty/output`      | Notification | `{ sessionId, dataBase64 }`                  | —               |
| `pty/exit`        | Notification | `{ sessionId, exitCode }`                    | —               |

`shell` defaults to `$SHELL` (or `/bin/bash` if unset). `cwd` defaults to
the sidecar's current working directory. `env` is a string→string map
merged onto the inherited environment.

`sessionId` is opaque to the sidecar; the UI mints UUIDs.

`pty/output` chunks are emitted as the sidecar reads them — no buffering
or aggregation. xterm.js handles partial UTF-8 sequences across chunks.

The Linux backend uses `posix_openpt` + `grantpt` + `unlockpt` to set up
the master/slave pair, then `posix_spawn` with `POSIX_SPAWN_SETSID` and
`posix_spawn_file_actions_addopen` to start the shell with the slave PTY
as its controlling terminal. The session-leader flag plus the slave PTY
being the first TTY opened is enough for the kernel to attach it as the
controlling terminal — no `TIOCSCTTY` ioctl is required.

### Consequences

- Good: M2 (panes) is purely additive — no signature changes.
- Good: byte-exact PTY pass-through; programs that emit binary (e.g.
  `tput`, image escape sequences) work without special-casing.
- Good: no fork-from-managed-code hazards; `posix_spawn` is designed for
  multi-threaded callers.
- Good: ConPty resize races are contained inside the sidecar, not the UI,
  when Windows lands.
- Good: focusing on Linux for M1 lets us land a working terminal quickly
  without paying the ConPty/macOS-signing tax up front.
- Bad: ~33% wire overhead on PTY traffic vs. raw bytes. Acceptable for
  interactive terminals; revisit if M3 (SSH bulk transfer) reveals
  throughput pressure.
- Bad: 50 ms resize debounce adds visible lag to drag-resize. Tunable.
- Bad: hand-written P/Invoke is more code than a NuGet wrapper would be,
  and the constants for `TIOCSWINSZ`, `O_NOCTTY`, `POSIX_SPAWN_SETSID`
  are platform-specific. The macOS port will need a parallel constants
  table.
- Neutral: macOS/Windows end-to-end behavior is not gated by M1 CI;
  regressions there will only surface when those platforms are exercised.
