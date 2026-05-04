#!/usr/bin/env bash
#
# scripts/check.sh — repo-level structural checks for FingerTrap.
#
# Usage: scripts/check.sh [--verbose]
#
# Exit codes:
#   0  all checks passed (warnings are informational only)
#   1  one or more errors found
#   2  precondition failure (missing dependency, wrong directory)
#
# Output follows the agent framework script-output convention:
#   OK    [name] message
#   SKIP  [name] message
#   WARN  [name] message
#   INFO  message
#   ERROR [name] message
#

set -euo pipefail

VERBOSE=false
if [[ "${1:-}" == "--verbose" ]]; then
    VERBOSE=true
fi

# Full helper set per the agent-framework script-output convention; some are
# unused in this script but kept for consistency with other repo scripts.
# shellcheck disable=SC2317
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
cd "$REPO_ROOT"

if [[ ! -d adrs ]]; then
    err "preconditions" "adrs/ directory not found under $REPO_ROOT"
    exit 2
fi

# ---- 1. Required top-level files ----
required_files=(
    ".editorconfig"
    ".gitattributes"
    ".gitignore"
    "README.md"
    "docs/milestones.md"
    "src-sidecar/Directory.Build.props"
    "src-sidecar/Directory.Packages.props"
    "src-sidecar/FingerTrap.slnx"
    "src-sidecar/src/FingerTrap.Sidecar/Program.cs"
    "src-sidecar/src/FingerTrap.Sidecar/Ipc/RpcSurface.cs"
    "src-ui/package.json"
    "src-ui/tsconfig.json"
    "src-ui/src/api.ts"
    "src-ui/src/transport.ts"
    "src-ui/src/main.ts"
    "src-tauri/Cargo.toml"
    "src-tauri/tauri.conf.json"
    "src-tauri/src/main.rs"
    "src-tauri/src/lib.rs"
    "src-tauri/src/sidecar.rs"
    ".github/workflows/ci.yml"
    ".github/workflows/lint-pr-title.yml"
    ".github/PULL_REQUEST_TEMPLATE.md"
    "scripts/dev-setup.sh"
    "scripts/check.sh"
)
for f in "${required_files[@]}"; do
    if [[ -f "$f" ]]; then
        ok "files" "$f"
    else
        err "files" "missing: $f"
        ((error_count++)) || true
    fi
done

# ---- 2. ADR numbering: sequential, no duplicates, no gaps ----
shopt -s nullglob
adr_files=(adrs/[0-9][0-9][0-9][0-9]-*.md)
shopt -u nullglob

if [[ ${#adr_files[@]} -eq 0 ]]; then
    err "adr-numbering" "no ADR files found under adrs/"
    ((error_count++)) || true
else
    numbers=()
    for adr in "${adr_files[@]}"; do
        base="$(basename "$adr")"
        n=$((10#${base:0:4}))
        numbers+=("$n")
    done
    IFS=$'\n' read -r -d '' -a sorted < <(printf '%s\n' "${numbers[@]}" | sort -n; printf '\0')

    duplicates=()
    gaps=()
    expected=1
    prev=-1
    for n in "${sorted[@]}"; do
        if [[ "$n" -eq "$prev" ]]; then
            duplicates+=("$n")
        fi
        while [[ "$expected" -lt "$n" ]]; do
            gaps+=("$expected")
            ((expected++))
        done
        if [[ "$n" -eq "$expected" ]]; then
            ((expected++))
        fi
        prev="$n"
    done

    if [[ ${#duplicates[@]} -gt 0 ]]; then
        err "adr-numbering" "duplicate ADR numbers: ${duplicates[*]}"
        ((error_count++)) || true
    fi
    if [[ ${#gaps[@]} -gt 0 ]]; then
        err "adr-numbering" "gaps in ADR numbering: ${gaps[*]}"
        ((error_count++)) || true
    fi
    if [[ ${#duplicates[@]} -eq 0 && ${#gaps[@]} -eq 0 ]]; then
        ok "adr-numbering" "${#sorted[@]} ADR(s), sequential, no duplicates"
        detail "numbers: ${sorted[*]}"
    fi
fi

# ---- 3. ADR template structure ----
required_headings=(
    "## Context and problem statement"
    "## Considered options"
    "## Decision outcome"
)
for adr in "${adr_files[@]}"; do
    base="$(basename "$adr")"
    missing=()
    for h in "${required_headings[@]}"; do
        if ! grep -qF "$h" "$adr"; then
            missing+=("$h")
        fi
    done
    if [[ ${#missing[@]} -eq 0 ]]; then
        ok "adr-template" "$base"
    else
        err "adr-template" "$base missing: ${missing[*]}"
        ((error_count++)) || true
    fi
done

# ---- 4. RpcSurface.cs <-> api.ts pairing (heuristic) ----
RPC="src-sidecar/src/FingerTrap.Sidecar/Ipc/RpcSurface.cs"
API="src-ui/src/api.ts"
if [[ -f "$RPC" && -f "$API" ]]; then
    rpc_methods=$(grep -cE '^\s*public\s+(async\s+)?Task' "$RPC" || true)
    api_types=$(grep -cE 'new\s+(RequestType|NotificationType)' "$API" || true)
    if [[ "$rpc_methods" -eq "$api_types" ]]; then
        ok "rpc-pairing" "RpcSurface methods=$rpc_methods, api.ts RequestType/NotificationType=$api_types"
    else
        warn "rpc-pairing" "RpcSurface methods=$rpc_methods, api.ts RequestType/NotificationType=$api_types (heuristic; verify manually per ADR-0003)"
        ((warn_count++)) || true
    fi
else
    skip "rpc-pairing" "RpcSurface.cs or api.ts not present yet"
fi

# ---- Summary ----
echo "=================================="
if [[ "$error_count" -eq 0 ]]; then
    echo "PASS — $error_count errors, $warn_count warnings"
    exit 0
else
    echo "FAIL — $error_count errors, $warn_count warnings"
    exit 1
fi
