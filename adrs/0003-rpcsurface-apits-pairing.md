# 0003 — `RpcSurface.cs` ↔ `api.ts` pairing rule

- Status: Accepted
- Date: 2026-04-26

## Context and problem statement

The IPC contract (ADR-0002) is a JSON-RPC method surface that exists on both
sides of the process boundary. Adding a method on the .NET side without a
corresponding TS facade is silently fine at compile time on both sides — the
TS code just never calls the new method, and the .NET method never receives a
call. The drift is invisible until someone tries to use the feature.

We need a structural rule that makes drift visible at code-review time.

## Considered options

- **Code generation from a shared schema (e.g., OpenRPC, custom IDL)** —
  guarantees both sides match. Adds a build step, generator, and
  schema-versioning overhead. Worthwhile at larger scale.
- **One-source-of-truth pairing convention (this ADR)** — `RpcSurface.cs`
  declares methods; `src-ui/src/api.ts` declares typed wrappers. PRs that
  modify one and not the other are visibly mismatched.
- **No rule** — accept silent drift. Rejected.

## Decision outcome

Chosen option: **One-source-of-truth pairing convention.**

`src-sidecar/src/FingerTrap.Sidecar/Ipc/RpcSurface.cs` is the canonical
declaration of every JSON-RPC method the sidecar exposes. `src-ui/src/api.ts`
is the canonical typed facade for those methods on the TS side.

Whenever a method is added, removed, or renamed in `RpcSurface.cs`, the
matching entry in `api.ts` must change in the same PR. Whenever a
notification is added on the .NET side, the matching subscription helper
must appear in `api.ts` in the same PR. This rule is enforced by review,
not tooling. `scripts/check.sh` provides a best-effort warning when
counts diverge.

When the surface grows enough to make manual pairing painful — likely
post-1.0 — supersede this with a code-generation ADR.

### Consequences

- Good: every IPC change is visible as a two-file diff. Reviewers know what
  to look for.
- Good: no generator, no extra build step, no schema file to keep in sync.
- Bad: enforced by convention, not the compiler. Gets harder as the surface
  grows.
- Neutral: the `scripts/check.sh` heuristic is informative, not authoritative.
