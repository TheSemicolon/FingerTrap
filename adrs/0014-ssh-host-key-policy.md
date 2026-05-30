# 0014 — Unified strict SSH host-key policy

- Status: Proposed
- Date: 2026-05-30

## Context and problem statement

FingerTrap will have **two SSH use-cases sharing one SSH.NET layer** (ADR-0013): interactive SSH terminals (M3) and the automated, often privileged theme-install workflow (ADR-0016). SSH.NET's `HostKeyEventArgs.CanTrust` **defaults to `true`** — if no `HostKeyReceived` handler is wired, every host key is silently accepted (a textbook MITM footgun).

The asymmetric danger: an interactive SSH session is often built with a lax "trust on first use, user clicks yes" posture. If that policy is established first (M3) and the install workflow is layered on the same `ISshService` later, the privileged path inherits the lax policy — and an MITM on a `sudo apt-get install` stream is effectively remote root.

## Considered options

- **Per-use-case policies** — relaxed for interactive terminals, strict for installs.
- **Single strict policy for all SSH** — one choke point, no exceptions.
- **Silent TOFU** — auto-accept and persist unknown keys without confirmation.

## Decision outcome

Chosen: **one strict host-key policy for all SSH, enforced at a single connect choke point, settled before any SSH code ships (M3).**

- `ISshService`/`SshExecutor` exposes a single `ConnectAsync(profile, ct)` that **every** caller (interactive terminal and install workflow) uses. There is no `ConnectUnchecked` path.
- A `HostKeyReceived` handler is **always** wired before `Connect()` and sets `CanTrust = false` first.
- The handler consults a **persisted known-hosts store**. On a match, proceed. On an unknown host, require **explicit SHA-256 fingerprint confirmation via the UI** before the key is accepted and persisted. **No silent TOFU.**
- This policy is load-bearing for the privileged install path, so it is ratified as its own decision rather than buried in the transport ADR.

### Consequences

- Good: the privileged install path cannot inherit a weaker interactive policy — there is only one policy.
- Good: protects against MITM on both interactive sessions and (critically) `sudo`-bearing install streams.
- Good: a single choke point is simple to audit and test.
- Bad: first connection to a new host requires a fingerprint-confirmation round-trip (UI work) rather than a silent connect.
- Neutral: the known-hosts store format/location is an implementation detail for M3 (a JSON store under the app's config dir is the likely shape).
