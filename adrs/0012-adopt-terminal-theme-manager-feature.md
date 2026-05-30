# 0012 — Adopt terminal-theme-manager as a FingerTrap feature

- Status: Proposed
- Date: 2026-05-30

## Context and problem statement

`terminal-theme-manager` (TTM) was a separate greenfield project — a cross-platform GUI to install oh-my-zsh/oh-my-bash and set the shell-framework theme (`ZSH_THEME`/`OSH_THEME`) on a target host that is local or remote over SSH. It had six accepted ADRs but **no code**. The question: should it be built standalone, or folded into FingerTrap?

The overlap is strong:

- Both are **.NET 10**. TTM's design needs exactly what FingerTrap's sidecar is built for: SSH command execution, SFTP, shell invocation, a command palette, and streamed output.
- **FingerTrap's SSH layer does not exist yet.** `ISshService` is an empty stub; SSH.NET is an ADR-0001 aspiration not yet in `Directory.Packages.props`; SSH is milestone **M3**. So TTM does not *duplicate* FingerTrap's SSH — TTM's transport design (and its security guardrails) is the material that **builds** M3.
- TTM has **no code to migrate** — zero switching cost.

The one historical blocker: TTM's own ADR-001 chose Avalonia specifically to *avoid* Tauri, because Tauri requires `webkit2gtk-4.1`, which is absent from Oracle Linux / RHEL 9 repos, and TTM assumed its GUI had to *run on* Oracle Linux. On re-evaluation, **Oracle Linux is only ever a remote SSH target** (a server distro themed from elsewhere), never a desktop GUI host. With that assumption corrected, the `webkit2gtk` constraint does not bind, and FingerTrap (Tauri) is a valid host.

## Considered options

- **A — Fold TTM into FingerTrap as a feature** (sidecar services + TS UI + RPC surface).
- **B — Shared .NET library** consumed by two separate apps (FingerTrap + a standalone TTM GUI).
- **C — Keep TTM fully separate**, share only conventions.

## Decision outcome

Chosen option: **A — fold TTM into FingerTrap as a feature.**

Why: shared stack; FingerTrap's three-process architecture is purpose-built for feature modules; TTM has nothing to migrate; and TTM *supplies* the SSH transport FingerTrap needs for M3 rather than duplicating it. The Tauri shell needs **zero Rust changes and no new capabilities** for the MVP (the feature is sidecar + TS only); SSH.NET is the only genuinely new dependency. Oracle-as-remote-only removes the constraint that previously favoured a separate Avalonia app.

This decision **supersedes** TTM's standalone direction. The migration of TTM's ADRs:

| TTM ADR | Disposition in FingerTrap |
|---|---|
| 001 GUI framework (Avalonia) | Superseded by this ADR — no standalone GUI. |
| 002 Execution transport | Migrated → **ADR-0013** (becomes the M3 SSH + local-exec spec). |
| 003 Theme model | Migrated → **ADR-0015**. |
| 004 Detection & install | Migrated → **ADR-0016**. |
| 005 Windows behaviour | Compatible — FingerTrap already defers Windows; TTM's client-only/WSL substance carries into FingerTrap's eventual Windows milestone. |
| 006 Packaging | Superseded by this ADR — TTM inherits FingerTrap's publish (ADR-0005) + Tauri bundle (ADR-0010). **Carry-over:** TTM-006's distribution requirement — *users must not bypass OS security to install* (macOS notarization, Windows signing) — transfers to FingerTrap's distribution, which does not sign/notarize yet. |

Cross-cutting decisions the integration adds: a **unified strict SSH host-key policy** (ADR-0014) and a consolidated **integration security guardrail set** (ADR-0017).

Feature surfacing (UI): a command-palette entry opens a wizard overlay (detect → plan → consent → install → report) with a read-only xterm.js pane for streamed install output. RPC surface grows under the ADR-0003 three-file pairing.

### Consequences

- Good: reuses FingerTrap's transport, packaging, CI, and command palette; TTM supplies the M3 SSH layer; no Rust changes for the MVP.
- Good: the originating design work (TTM ADRs 002/003/004) is preserved as FingerTrap feature ADRs, not lost.
- Bad: couples a privileged-install workflow into the same process as the interactive terminal — new trust boundaries (ADR-0017) and a single host-key policy obligation (ADR-0014).
- Bad: the macOS-notarization / Windows-signing requirement now lands on FingerTrap's distribution pipeline.
- Neutral: TTM's repo becomes a design-record archive; its superseded ADRs point here.
