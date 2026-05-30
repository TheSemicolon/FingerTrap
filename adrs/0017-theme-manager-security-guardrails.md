# 0017 — Theme-manager integration security guardrails

- Status: Proposed
- Date: 2026-05-30

## Context and problem statement

Folding an automated, privileged install workflow (ADR-0016) into FingerTrap's Tauri + sidecar terminal app introduces trust boundaries that the originating standalone design (`terminal-theme-manager`) did not have, because credentials and commands now cross the JSON-RPC-over-stdio IPC and coexist in one process with an interactive PTY/terminal feature. This ADR consolidates the guardrails the integration must hold — both those carried forward from TTM's design and those newly required by the merge.

## Considered options

- **Implicit / ad-hoc** — rely on each feature ADR to mention its own guardrails.
- **One consolidated guardrail ADR** — a single auditable list the implementation and code review enforce.

## Decision outcome

Chosen: **a single consolidated guardrail set**, mandatory for the theme-manager feature. Host-key policy is ADR-0014; the rest follow.

**Carried forward from the TTM design (enforced at FingerTrap's boundaries):**

1. **Upload-then-execute; never inline interpolation** of user- or remote-supplied values into command strings.
2. **Allowlist-validate at the RPC handler boundary** — theme name `[a-zA-Z0-9_-]+`, hostname — *and treat remote-host-returned values* (`$HOME`, username, shell path) *as untrusted* before embedding them in any script body or SFTP path.
3. **`sudo` password via stdin only** (`sudo -S`, written to the command's stdin stream — never `echo … | sudo`), never interpolated, **never logged**; held as a zeroable `char[]`/`byte[]` and cleared after use; **`sudo -k` after the install sequence** to drop the timestamp cache (mandatory even if the interactive session stays open).
4. **NOPASSWD only on a fixed-path wrapper script — never on `apt-get`/`apt`/`dnf` directly** (those are GTFOBins-class passwordless root regardless of scope).
5. **Symlink-safe SFTP writes** — check `SftpFileAttributes.IsSymbolicLink` before writing `~/.zshrc`/`~/.bashrc`; refuse if the resolved target leaves the home prefix.

**Newly required by the merge:**

6. **Tauri capability scoping for install-triggering RPC methods.** xterm.js may render attacker-controlled bytes; any WebView script that can reach `sidecar_write` could craft an install/connect RPC. The sidecar's RPC handler is the authorization boundary, and the install-related methods should be gated by a separate, narrower capability rather than riding the default sidecar channel.
7. **IPC credential transit is an accepted, documented gap.** Credentials cross the IPC as JSON strings (not zeroable memory). Mitigate per hop: the TS UI reads from an `<input type="password">`, sends one RPC call, discards the reference; the sidecar copies the received string into a `char[]`, uses it, and zeros it; the zero-after-use property applies to **sidecar post-receipt handling only**, not transit. **The `sudo` password is never persisted** (an SSH key/passphrase in the OS keychain is acceptable; a sudo password is always prompted and ephemeral).
8. **Diagnostic-logging redaction must cover the Rust stderr relay.** The sidecar routes diagnostics to stderr (ADR-0002) and `sidecar.rs` re-emits them through the host process's stderr (visible in dev terminals and OS crash logs). No log statement touching `ConnectionInfo`/auth/credential-derived values may include a secret. Use structured logging with named parameters and redacted credential-bearing types; enforce via a code-review gate on connection-info paths.
9. **Fixed interpreter for scripted remote commands.** Use an absolute-path interpreter (`/bin/sh`, or a probe-detected `bash`), **never the remote `$SHELL`** — using the remote environment's shell for scripted install commands is a remote-value-injection vector (distinct from interactive `pty/spawn`, which may use `$SHELL`).

### Consequences

- Good: one auditable, code-review-enforceable list; the privileged path is hardened against the merge-specific exposures.
- Good: makes explicit which TTM guarantees weaken across the IPC boundary (credential transit) rather than silently assuming them.
- Bad: real implementation cost — capability scoping, redaction discipline, and the credential-handling dance are non-trivial and must land with the feature, not after.
- Neutral: derived from a security review of the integration (verdict NEEDS_CHANGES); these guardrails are the conditions under which that verdict clears.
