#!/usr/bin/env bash
#
# scripts/dev-setup.sh — verify and optionally install FingerTrap dev dependencies.
#
# Usage:
#   scripts/dev-setup.sh             # default: --check (read-only)
#   scripts/dev-setup.sh --check     # check only, no system changes
#   scripts/dev-setup.sh --install   # install missing dependencies
#
# Exit codes:
#   0  all dependencies present (or installed successfully in --install mode)
#   1  one or more dependencies missing (in --check mode) or install failed
#   2  precondition failure (unsupported platform, bad arguments)
#
# Tools verified:
#   - .NET 10 SDK         (sidecar: dotnet build / test)
#   - Node.js >= 22       (UI: vite, eslint, typescript)
#   - corepack            (ships with Node; manages pnpm)
#   - pnpm 10             (UI package manager)
#   - rustup + cargo      (Tauri shell: cargo check / clippy / fmt)
#   - Tauri Linux deps    (libwebkit2gtk-4.1-dev libappindicator3-dev librsvg2-dev patchelf)
#
# Platforms:
#   - Debian/Ubuntu Linux: full --install support via apt and rustup
#   - macOS:               --install supports rustup, pnpm; .NET prints guidance
#   - Windows:             prints guidance only; manual install via winget
#
# Output follows the agent-framework script-output convention.

set -euo pipefail

MODE="check"
case "${1:-}" in
    --install) MODE="install" ;;
    --check | "") MODE="check" ;;
    *)
        echo "usage: $0 [--check|--install]" >&2
        exit 2
        ;;
esac

# shellcheck disable=SC2317
{
ok()    { echo "OK    [$1] $2"; }
skip()  { echo "SKIP  [$1] $2"; }
warn()  { echo "WARN  [$1] $2"; }
info()  { echo "INFO  $*"; }
err()   { echo "ERROR [$1] $2" >&2; }
}

OS_KIND="$(uname -s)"
case "$OS_KIND" in
    Linux)  PLATFORM="linux" ;;
    Darwin) PLATFORM="macos" ;;
    MINGW* | MSYS* | CYGWIN*) PLATFORM="windows" ;;
    *)
        err "platform" "unsupported OS: $OS_KIND"
        exit 2
        ;;
esac

if [[ "$PLATFORM" == "linux" ]] && [[ -f /etc/os-release ]]; then
    # shellcheck disable=SC1091
    . /etc/os-release
    DISTRO="${ID:-unknown}"
else
    DISTRO=""
fi

errors=0
warns=0
installs=0

info "platform: $PLATFORM ${DISTRO:+($DISTRO)}, mode: $MODE"

# --------------------------------------------------------------------------
# .NET SDK — required for src-sidecar/
# --------------------------------------------------------------------------
check_dotnet() {
    if command -v dotnet >/dev/null 2>&1; then
        local ver
        ver="$(dotnet --version 2>/dev/null || echo unknown)"
        if [[ "$ver" =~ ^10\. ]]; then
            ok "dotnet" "$ver"
            return 0
        else
            warn "dotnet" "$ver — .NET 10 required for the sidecar"
            ((warns++)) || true
            return 1
        fi
    fi

    if [[ "$MODE" != "install" ]]; then
        warn "dotnet" "not installed"
        ((warns++)) || true
        return 1
    fi

    case "$PLATFORM" in
        linux)
            info "installing .NET 10 SDK via apt (Debian/Ubuntu)"
            sudo apt-get update
            sudo apt-get install -y dotnet-sdk-10.0
            ((installs++)) || true
            ok "dotnet" "$(dotnet --version)"
            ;;
        macos)
            warn "dotnet" "install manually: brew install --cask dotnet-sdk (or download from https://dotnet.microsoft.com)"
            ((warns++)) || true
            ;;
        windows)
            warn "dotnet" "install manually: winget install Microsoft.DotNet.SDK.10"
            ((warns++)) || true
            ;;
    esac
}

# --------------------------------------------------------------------------
# Node.js — required for src-ui/
# --------------------------------------------------------------------------
check_node() {
    if command -v node >/dev/null 2>&1; then
        local ver
        ver="$(node --version 2>/dev/null)"
        local major="${ver#v}"
        major="${major%%.*}"
        if [[ "$major" -ge 22 ]]; then
            ok "node" "$ver"
            return 0
        else
            warn "node" "$ver — Node 22+ required (use nvm or volta to upgrade)"
            ((warns++)) || true
            return 1
        fi
    fi

    case "$PLATFORM" in
        linux | macos)
            warn "node" "not installed; install nvm or volta from https://github.com/nvm-sh/nvm or https://volta.sh"
            ;;
        windows)
            warn "node" "install manually: winget install OpenJS.NodeJS.LTS"
            ;;
    esac
    ((warns++)) || true
}

# --------------------------------------------------------------------------
# corepack + pnpm — UI package manager
# --------------------------------------------------------------------------
check_pnpm() {
    if command -v pnpm >/dev/null 2>&1; then
        ok "pnpm" "$(pnpm --version)"
        return 0
    fi

    if ! command -v corepack >/dev/null 2>&1; then
        warn "pnpm" "corepack not found — install Node.js 22+ first"
        ((warns++)) || true
        return 1
    fi

    if [[ "$MODE" != "install" ]]; then
        warn "pnpm" "not enabled — run 'corepack enable pnpm' or rerun with --install"
        ((warns++)) || true
        return 1
    fi

    info "enabling pnpm via corepack"
    corepack enable pnpm
    ((installs++)) || true
    ok "pnpm" "$(pnpm --version)"
}

# --------------------------------------------------------------------------
# rustup + cargo — required for src-tauri/
# --------------------------------------------------------------------------
check_rust() {
    if command -v cargo >/dev/null 2>&1 && command -v rustup >/dev/null 2>&1; then
        ok "rust" "$(rustc --version 2>/dev/null) / rustup $(rustup --version 2>/dev/null | head -1)"
        return 0
    fi

    # Installed but not on PATH? Common case after a fresh rustup install.
    if [[ -x "$HOME/.cargo/bin/cargo" && -x "$HOME/.cargo/bin/rustup" ]]; then
        warn "rust" "installed at \$HOME/.cargo/bin but not on PATH — run: . \"\$HOME/.cargo/env\""
        ((warns++)) || true
        return 1
    fi

    if [[ "$MODE" != "install" ]]; then
        warn "rust" "rustup/cargo not installed (https://rustup.rs)"
        ((warns++)) || true
        return 1
    fi

    case "$PLATFORM" in
        linux | macos)
            info "installing rustup with default stable toolchain (--profile minimal)"
            curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs \
                | sh -s -- -y --default-toolchain stable --profile minimal --no-modify-path
            export PATH="$HOME/.cargo/bin:$PATH"
            rustup component add rustfmt clippy
            ((installs++)) || true
            ok "rust" "$(rustc --version) / rustup $(rustup --version | head -1)"
            warn "rust" "add ~/.cargo/bin to PATH in your shell rc (e.g. ~/.bashrc): export PATH=\"\$HOME/.cargo/bin:\$PATH\""
            ((warns++)) || true
            ;;
        windows)
            warn "rust" "install manually: winget install Rustlang.Rustup"
            ((warns++)) || true
            ;;
    esac
}

# --------------------------------------------------------------------------
# Tauri Linux system dependencies
# --------------------------------------------------------------------------
check_tauri_linux_deps() {
    if [[ "$PLATFORM" != "linux" ]]; then
        skip "tauri-linux-deps" "$PLATFORM — not applicable"
        return 0
    fi

    local pkgs=(
        libwebkit2gtk-4.1-dev
        libappindicator3-dev
        librsvg2-dev
        patchelf
    )

    if ! command -v dpkg >/dev/null 2>&1; then
        warn "tauri-linux-deps" "dpkg not found; install Tauri deps manually for your distro"
        ((warns++)) || true
        return 1
    fi

    local missing=()
    for p in "${pkgs[@]}"; do
        if ! dpkg -s "$p" >/dev/null 2>&1; then
            missing+=("$p")
        fi
    done

    if [[ ${#missing[@]} -eq 0 ]]; then
        ok "tauri-linux-deps" "all installed (${pkgs[*]})"
        return 0
    fi

    if [[ "$MODE" != "install" ]]; then
        warn "tauri-linux-deps" "missing: ${missing[*]}"
        ((warns++)) || true
        return 1
    fi

    info "installing Tauri Linux deps via apt"
    sudo apt-get update
    sudo apt-get install -y "${missing[@]}"
    ((installs++)) || true
    ok "tauri-linux-deps" "installed: ${missing[*]}"
}

# --------------------------------------------------------------------------
# Run checks
# --------------------------------------------------------------------------
check_dotnet           || true
check_node             || true
check_pnpm             || true
check_rust             || true
check_tauri_linux_deps || true

# --------------------------------------------------------------------------
# Summary
# --------------------------------------------------------------------------
echo "=================================="
if [[ "$errors" -gt 0 ]]; then
    echo "FAIL — $errors errors, $warns warnings, $installs installs"
    exit 1
elif [[ "$warns" -gt 0 && "$MODE" == "check" ]]; then
    echo "INCOMPLETE — $errors errors, $warns warnings (run with --install to fix)"
    exit 1
else
    echo "PASS — $errors errors, $warns warnings, $installs installs"
    exit 0
fi
