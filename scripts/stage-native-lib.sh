#!/usr/bin/env bash
#
# scripts/stage-native-lib.sh — fail-fast gate for the Tauri production bundle.
#
# Invoked by tauri.conf.json build.beforeBundleCommand during `tauri build`.
# Validates that the companion native library required by the .NET sidecar
# (libporta_pty.{dylib,so}) is present in src-tauri/binaries/ before the
# Tauri bundler assembles the .app / .deb / .AppImage / .rpm package.
#
# NOT used during `cargo tauri dev` — src-tauri/build.rs handles dev mode by
# copying the companion lib next to the sidecar in target/<profile>/.
#
# Why this is a gate, not a copier: the actual placement inside the bundle
# is controlled declaratively by tauri.macos.conf.json (bundle.macOS.frameworks)
# and tauri.linux.conf.json (bundle.resources). Tauri's bundler reads its
# manifest after beforeBundleCommand runs, so a copy here would not affect
# the bundle. This script's job is to make a missing source file a loud
# pre-bundle failure rather than a confusing post-bundle runtime crash.
# See ADR-0010 for the full mechanism design.
#
# Required environment (injected by Tauri CLI per the v2 hooks contract):
#   TAURI_ENV_PLATFORM       darwin | linux | windows
#   TAURI_ENV_TARGET_TRIPLE  e.g. aarch64-apple-darwin (informational only)
#
# Exit codes:
#   0  source file present, bundle may proceed
#   1  source file missing — bundle would produce a broken sidecar
#   2  environment failure (not invoked by tauri build, unknown platform)

set -euo pipefail

VERBOSE=false
if [[ "${1:-}" == "--verbose" ]]; then
    VERBOSE=true
fi

# shellcheck disable=SC2317,SC2329
{
ok()     { echo "OK    [$1] $2"; }
skip()   { echo "SKIP  [$1] $2"; }
warn()   { echo "WARN  [$1] $2"; }
info()   { echo "INFO  $*"; }
err()    { echo "ERROR [$1] $2" >&2; }
detail() { if $VERBOSE; then echo "      $*"; fi; }
}

error_count=0
warn_count=0

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

info "Staging gate for Tauri bundle companion native library"
detail "repo root: $REPO_ROOT"

if [[ -z "${TAURI_ENV_PLATFORM:-}" ]]; then
    err "env" "TAURI_ENV_PLATFORM unset — invoke via 'tauri build', not directly"
    exit 2
fi
detail "platform: $TAURI_ENV_PLATFORM"
detail "target triple: ${TAURI_ENV_TARGET_TRIPLE:-<unset>}"

case "$TAURI_ENV_PLATFORM" in
    darwin)
        lib_name="libporta_pty.dylib"
        ;;
    linux)
        lib_name="libporta_pty.so"
        ;;
    windows)
        ok "stage-lib" "Windows uses ConPTY via kernel32; no companion lib required"
        echo "=================================="
        echo "PASS — 0 errors, 0 warnings"
        exit 0
        ;;
    *)
        err "env" "unrecognised TAURI_ENV_PLATFORM: $TAURI_ENV_PLATFORM"
        exit 2
        ;;
esac

src="$REPO_ROOT/src-tauri/binaries/$lib_name"
if [[ -f "$src" ]]; then
    size=$(wc -c < "$src" | tr -d ' ')
    ok "stage-lib" "$lib_name present ($size bytes)"
    detail "source: $src"
else
    err "stage-lib" "$lib_name missing at $src"
    err "stage-lib" "Run 'dotnet publish src-sidecar/src/FingerTrap.Sidecar/FingerTrap.Sidecar.csproj' and stage the companion lib into src-tauri/binaries/ — see CONTRIBUTING.md"
    ((error_count++)) || true
fi

echo "=================================="
if [[ "$error_count" -eq 0 ]]; then
    echo "PASS — $error_count errors, $warn_count warnings"
    exit 0
else
    echo "FAIL — $error_count errors, $warn_count warnings"
    exit 1
fi
