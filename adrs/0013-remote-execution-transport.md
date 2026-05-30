# 0013 — Remote execution transport and command model

- Status: Proposed
- Date: 2026-05-30

## Context and problem statement

The theme-manager feature (ADR-0012) needs to run commands and edit files on a target host that is **local or remote over SSH**, and the SSH half is also FingerTrap's M3 deliverable. Two distinct execution shapes are required, neither of which the existing `PtyService` (ADR-0006) provides:

- **Non-interactive command capture** — run a command, capture stdout/stderr/exit-code as a result. `PtyService` is an interactive pseudoterminal stream (event-driven bytes, no exit-code result); the two are orthogonal and do not share an implementation.
- **SSH command + SFTP** — `ISshService` is currently an empty stub and SSH.NET is not yet a dependency. This ADR defines that layer.

This migrates the design of `terminal-theme-manager` ADR-002 (superseded), dropping its Avalonia/MVVM threading notes (a TS-UI concern here).

## Considered options

- **SSH transport:** managed SSH.NET (Renci.SshNet) vs. shelling out to the system `ssh` client.
- **Abstraction:** a single transport-agnostic interface with local + SSH implementations vs. separate per-transport APIs.
- **`LocalExecutor` placement:** reuse/extend `PtyService` vs. a standalone non-interactive executor.

## Decision outcome

Chosen:

- A single **`IRemoteExecutor`** abstraction (in `FingerTrap.Sidecar.Abstractions`, replacing/absorbing the `ISshService` stub) with two implementations:
  - **`LocalExecutor`** — `System.Diagnostics.Process`, non-interactive capture. **Stands alongside `PtyService`, not through it** (different shape). Reads stdout/stderr concurrently before `WaitForExitAsync` (deadlock-safe); streaming variant pumps `ReadLineAsync` into an unbounded `Channel<OutputLine>` (not `BeginOutputReadLine`).
  - **`SshExecutor`** — wraps SSH.NET `SshClient` + `SftpClient`; **this is the M3 SSH layer.** Registered transient (per-connection lifetime). Exit code is nullable (`int?` — null = killed by signal).
- **SSH.NET** (managed) over shelling out — a coherent in-app connection/host-key UX, SFTP for rc-file edits, no dependency on a system `ssh` client. Added to `Directory.Packages.props`; regenerate lock files RID-agnostic (ADR-0009) after adding.
- **Upload-then-execute**: multi-step scripts are written to a temp file, transferred, and run by fixed path — never composed by inline interpolation.
- **Streaming**: `IAsyncEnumerable<OutputLine>` on the RPC method, which StreamJsonRpc emits as notifications (idiomatic; maps cleanly to the `Channel<OutputLine>` pump).
- **SSH session lifecycle is RPC-driven**: `theme/connect` returns a `sessionId`; downstream methods carry it; the `RpcSurface` holds a `ConcurrentDictionary<sessionId, SshExecutor>` (mirroring the PTY `_sessions` pattern). Credentials are not re-sent per call.

Host-key verification policy is specified in **ADR-0014**; the full security guardrail set in **ADR-0017**.

### Consequences

- Good: builds M3's SSH layer and the theme-manager's transport in one coherent abstraction; install/theme logic is transport-agnostic.
- Good: SFTP enables safe rc-file read-modify-write (ADR-0015); no system `ssh` prerequisite.
- Bad: SSH.NET pulls in `Portable.BouncyCastle`, whose `[RequiresDynamicCode]` paths complicate the eventual trim/AOT step (M8) — root BouncyCastle whole when trimming; untrimmed today is fine.
- Bad: `LocalExecutor` is net-new code, separate from `PtyService` — two execution models in the sidecar.
- Neutral: SSH.NET's `HostKeyReceived` defaults to trusting any key — made safe by ADR-0014.
