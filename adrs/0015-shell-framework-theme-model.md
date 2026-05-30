# 0015 — Shell-framework theme model

- Status: Proposed
- Date: 2026-05-30

## Context and problem statement

The theme-manager feature (ADR-0012) must define concretely what a "terminal theme" is, which layer it manages, where the catalog comes from, and how a theme is applied safely. "Terminal theme" is ambiguous — it can mean the shell-framework prompt theme or the terminal-emulator colour palette, which are independent layers owned by different processes.

This migrates `terminal-theme-manager` ADR-003 (superseded). The execution mechanism is ADR-0013 (`IRemoteExecutor` + SFTP); the executor runs non-interactive, so rc files are not sourced.

## Considered options

- **Which layer:** shell-framework theme only; framework + emulator colour schemes; both.
- **Which themes:** bundled (framework-shipped) only; bundled + external git-clone themes (powerlevel10k, spaceship).
- **Catalog source:** live target enumeration only; static built-in; runtime-fetched; hybrid.

## Decision outcome

Chosen:

- **"Theme" = the oh-my-zsh `ZSH_THEME` / oh-my-bash `OSH_THEME` framework theme only.** Terminal-emulator colour schemes are out of scope (client-side, no common cross-emulator format, incoherent over SSH — the remote host has no emulator). OSC palette escapes are not a substitute (session-only, unreliable, broken under tmux). Emulator colour-scheme management is a named future feature, not this ADR.
- **Bundled themes only** for the MVP — pure rc-file edits, no new trust surface. External git-clone themes (powerlevel10k etc.) are deferred (they fetch+source third-party code, need `git` on the target, and powerlevel10k's wizard needs a TTY).
- **Hybrid catalog**: a curated **static catalog embedded** in the sidecar (browsable offline) **+ live `ls` enumeration** over `IRemoteExecutor` to mark which are installed on the target. **No runtime fetch of a catalog or theme code** (a network-injection vector into rc-file writes).
- **Apply mechanics**: SFTP **read-modify-write** of the rc line, with **backup → upload → `zsh -n`/`bash -n` syntax check → rollback on failure**. Idempotent line edit (remove-all-then-prepend; leave commented variants; edit only the canonical `~/.zshrc`/`~/.bashrc`). Validate the theme exists before writing (fail closed). Exclude the `random` meta-theme. Handle omz-flat (`themes/<n>.zsh-theme`) vs omb-nested (`themes/<n>/<n>.theme.bash`) layouts. Symlink-safe writes per ADR-0017.
- **Model**: immutable `ThemeInfo` record; `System.Text.Json` source-gen; the static catalog is an embedded JSON resource; "installed-on-target" is runtime state, not part of the record. Font requirement is **client-side metadata + a warning, never an action** (the Nerd/Powerline font must be where the emulator renders — the user's machine — not on the SSH target).

### Consequences

- Good: one coherent, durable, transport-symmetric layer; no runtime network access; minimal input-allowlist (`[a-zA-Z0-9_-]+`, no slash needed).
- Good: backup + `-n` + rollback makes theme application safe and reversible.
- Bad: the app is a "theme manager" but the MVP does not manage emulator colour palettes — a colloquial-expectation gap, documented as deferred.
- Neutral: the static catalog can drift from upstream between releases; the live-enumeration overlay backstops it.
