# 0001 — Three-process architecture: Tauri shell + TS UI + .NET sidecar

- Status: Accepted
- Date: 2026-04-26

## Context and problem statement

FingerTrap needs to host PTY sessions, SSH/SFTP, git, and rich integrations
(GitHub, Azure DevOps) inside a desktop window. Each of those three concerns
has a natural home: a window/WebView host, a UI runtime, and a backend runtime
with mature libraries for terminals and remote protocols.

Putting all of this in one runtime forces a worst-of-three trade: WebView
hosts can't run native PTY libraries; .NET cross-platform UI options are
limited; embedding both in Electron means large binaries, JavaScript-bound
SSH libraries, and a more painful supply-chain story.

## Considered options

- **Electron + Node + native modules** — single runtime. Native PTY/SSH via
  Node addons. Large bundle, supply-chain surface, less mature .NET-equivalent
  libraries for the integration set.
- **Avalonia (single .NET app)** — one runtime, one language. Terminal surface
  has to be reimplemented or licensed; the xterm.js feature set is hard to
  match. xterm.js owns the terminal surface in this design — replacing it is
  out of scope.
- **Tauri shell + TS UI + .NET sidecar (this ADR)** — three runtimes, each
  doing what it's best at. Cost is one IPC contract.
- **Browser app + cloud backend** — rejected; FingerTrap is local-first.

## Decision outcome

Chosen option: **Tauri shell + TS UI + .NET sidecar**.

Tauri provides a small native window with a system WebView. The UI lives
inside that WebView. The sidecar is spawned as a child process and owns the
business logic — Pty.Net for terminals, SSH.NET for remote, LibGit2Sharp for
git, Octokit and the Azure DevOps SDK for status sources. The Rust shell is
deliberately dumb: window management, sidecar lifecycle, and a stdio bridge.

The cost is one IPC contract spanning two languages. We accept it because it
buys us xterm.js as the terminal surface, a mature .NET ecosystem for the
backend, and a small native bundle.

### Consequences

- Good: each runtime does what it does best. Sidecar logic is testable
  without a window. UI changes don't require a backend rebuild and vice versa.
- Good: Pty.Net + SSH.NET + LibGit2Sharp + Octokit are first-class .NET
  libraries with active maintenance.
- Bad: cross-process IPC is slower than in-process calls and adds a
  serialization boundary. We mitigate by using `Content-Length`-framed
  JSON-RPC and by keeping the surface small.
- Bad: three toolchains in CI (dotnet, node/pnpm, rust/cargo).
- Neutral: distribution is a single Tauri bundle that includes the sidecar as
  an `externalBin` — users install one app.
