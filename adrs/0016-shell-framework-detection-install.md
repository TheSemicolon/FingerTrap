# 0016 — Shell-framework detection and install strategy

- Status: Proposed
- Date: 2026-05-30

## Context and problem statement

The theme-manager feature (ADR-0012) must detect what is present on a target host, decide which framework to use, and install oh-my-zsh/oh-my-bash plus any missing prerequisites — across Debian 13 (apt), Oracle Linux 9 (dnf), and macOS (Homebrew). This is where privileged installation happens, so it is the focal point for the elevation guardrails.

This migrates `terminal-theme-manager` ADR-004 (superseded), running over `IRemoteExecutor` (ADR-0013). The executor is non-interactive, so `$SHELL`/`$ZSH`/`omz` are unavailable — detection uses direct probes and SFTP.

## Considered options

- **Install method:** upstream `curl|sh` installer vs. app-composed `git clone` vs. vendored/pinned installer.
- **Prerequisite scope:** install missing prerequisites with elevation vs. detect-and-report only.
- **Elevation:** connection-user-is-root vs. `sudo -S` (stdin) vs. NOPASSWD sudoers.

## Decision outcome

Chosen:

- **Flow: detect → plan → consent → install → report.** An unprivileged probe (uploaded once) returns OS family + package manager (`/etc/os-release` ID/ID_LIKE, `uname -s` for macOS), `command -v` + versions (zsh/bash/git/curl), login shell (`getent passwd` → `dscl` on macOS, never `$SHELL`), framework sentinels (`~/.oh-my-zsh/oh-my-zsh.sh`, `~/.oh-my-bash/oh-my-bash.sh`), and privilege state (`id -u`, `sudo -n true`). The sidecar computes a plan; the UI presents it; **nothing privileged or destructive runs without an explicit consent round-trip.**
- **Framework default** matches the login shell (zsh → oh-my-zsh; bash → oh-my-bash; macOS → oh-my-zsh), always user-overridable.
- **Install method: upstream `curl|sh` installer** (`CHSH=no RUNZSH=no KEEP_ZSHRC=yes` / `--unattended`), gated by the sentinel pre-check (the installers are not idempotent — exit 1 if the dir exists; a dir-without-sentinel is a partial install → error, not resume). **Mandatory mitigations** for its known gaps: with `KEEP_ZSHRC=yes` the installer leaves omz *inert* if `~/.zshrc` exists, so the app verifies/patches the `source` line via the ADR-0015 rc flow; oh-my-bash *unconditionally replaces* `~/.bashrc`, so the app preserves/re-merges the prior content. HTTPS-only fetch; installer URLs are fixed allowlisted constants. (App-composed `git clone` was the lower-supply-chain-risk alternative and is recorded as the fallback if the posture tightens.)
- **Install missing prerequisites with elevation**: Debian `apt-get` (`DEBIAN_FRONTEND=noninteractive`, `-o DPkg::Lock::Timeout=300`); Oracle Linux **enable EPEL via `oracle-epel-release-el9`** (zsh is absent from BaseOS/AppStream) then `dnf -y`; macOS `brew` **as the connection user, never `sudo brew`** (gate on Xcode CLT). Graceful degradation with actionable errors when no PM / no network / no privilege.
- **`chsh` is a separate, explicitly-consented step** (`CHSH=no` during install) — hard switch (`usermod -s`/`chsh`, macOS uses `/bin/zsh`) or no-privilege soft switch (`exec zsh -l` in `~/.bash_profile`).

Elevation and injection guardrails (including **NOPASSWD wrapper-only, never `apt-get`/`dnf` directly**, `sudo -S` via stdin + `sudo -k`, untrusted remote `$HOME`, fixed interpreter) are consolidated in **ADR-0017**; host-key policy in **ADR-0014**.

### Consequences

- Good: turnkey across Debian/Oracle/macOS; idempotent, consented, least-privilege; per-step reporting.
- Bad: `curl|sh` fetches+executes unpinned remote code at install time — an accepted supply-chain tradeoff (HTTPS + allowlisted URLs); the app still owns rc-patching to close the installer's gaps.
- Bad: the MVP carries the full cross-distro elevation surface (GTFOBins-aware sudoers, EPEL, Homebrew/Xcode-CLT).
- Neutral: both installers and `git` require outbound network on the target; air-gapped hosts are unsupported in the MVP.
