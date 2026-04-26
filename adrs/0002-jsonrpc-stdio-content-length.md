# 0002 — JSON-RPC 2.0 over stdio with Content-Length framing

- Status: Accepted
- Date: 2026-04-26

## Context and problem statement

The Tauri shell, TS UI, and .NET sidecar (ADR-0001) need a wire protocol. The
sidecar is spawned as a child process; its stdin and stdout are owned by the
Rust shell. The shell forwards bytes between the UI (via Tauri IPC) and the
sidecar (via the child's stdio).

The contract has to be language-agnostic, support request/response and
server-initiated notifications, and have stable, well-supported client
libraries on both sides.

## Considered options

- **Named pipes / Unix domain sockets** — slightly faster, more setup, harder
  cross-platform parity (Windows named pipes ≠ Unix sockets), and the Tauri
  sidecar primitive is built around stdio.
- **WebSockets over a localhost port** — needs a port, firewall surface,
  unnecessary network stack.
- **Custom binary protocol** — small payloads, but we lose tooling and have
  to write framing for nothing.
- **JSON-RPC 2.0 over stdio with newline framing** — simple, but
  non-standard among the .NET/TS libraries we'd want to use.
- **JSON-RPC 2.0 over stdio with `Content-Length` (HTTP-style) framing**
  (this ADR) — the Language Server Protocol convention. Native to
  `StreamJsonRpc`'s `HeaderDelimitedMessageHandler` and to the
  `vscode-jsonrpc` npm package.

## Decision outcome

Chosen option: **JSON-RPC 2.0 over stdio with `Content-Length` framing**.

`StreamJsonRpc` (.NET) and `vscode-jsonrpc` (TS) are wire-compatible with
zero configuration on this framing. Both packages handle partial-message
buffering and content-encoding correctly. The protocol is stable, audited,
and shipped at scale by Visual Studio and every LSP-based editor.

The wire format is:

```
Content-Length: 58\r\n
\r\n
{"jsonrpc":"2.0","id":1,"method":"ping","params":["hi"]}
```

### Consequences

- Good: TS and .NET sides require no framing code of our own.
- Good: standard request/response and notification semantics.
- Good: tooling — log streams are inspectable as text, mostly.
- Bad: **stdout pollution is fatal.** Any `Console.Write*` call in the
  sidecar process (including from third-party libraries) corrupts framing
  silently. The remote sees malformed messages with no useful error. Mitigated
  by routing all logging to stderr and clearing default logging providers
  before `JsonRpc.StartListening()`. This is a permanent operational
  constraint and is documented in `src-sidecar/src/FingerTrap.Sidecar/Program.cs`.
- Neutral: framing changes would be a breaking wire-protocol change requiring
  coordinated TS + .NET releases. Not anticipated.
