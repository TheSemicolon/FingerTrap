#!/usr/bin/env bash
#
# hooks/check-lock-shape.sh — verify packages.lock.json files are RID-agnostic.
#
# Usage:
#   hooks/check-lock-shape.sh             # check working-tree lock files
#   hooks/check-lock-shape.sh --staged    # check git-staged lock files (pre-commit)
#
# Exit codes:
#   0  all lock files are RID-agnostic
#   1  one or more lock files contain RID-specific graph entries
#   2  precondition failure (not in a git repo when --staged, etc.)
#
# Why this script exists
# ----------------------
# .NET's `dotnet publish -r <rid> -p:PublishSingleFile=true` performs an
# implicit RID-aware restore that writes `net<tfm>/<rid>` graph entries
# into packages.lock.json. CI's `dotnet restore --locked-mode` step (run
# without -r per the workflow design) then fails with NU1004 across all
# platforms.
#
# The contamination signal is unambiguous: a clean RID-agnostic lock file
# has only `"net<tfm>"` (no slash) as a TFM key. Any `"net<tfm>/<rid>"`
# entry means a publish-side restore wrote to the file. The fix on the
# developer's side is to run `dotnet restore` (no -r, no --force-evaluate)
# from src-sidecar/ to regenerate, then commit the cleaned files.
#
# This script does NOT key on package names like `Microsoft.NET.ILLink.Tasks`
# — that package is legitimately added when `<PublishTrimmed>true</...>` is
# enabled, and a name-based filter would silently strip a real entry.
# See ADR-0009 for the supply-chain analysis.
#
# Output follows the agent-framework script-output convention.

set -euo pipefail

MODE="working"
case "${1:-}" in
    --staged) MODE="staged" ;;
    --working | "") MODE="working" ;;
    *)
        echo "usage: $0 [--working|--staged]" >&2
        exit 2
        ;;
esac

# shellcheck disable=SC2317,SC2329
{
ok()    { echo "OK    [$1] $2"; }
skip()  { echo "SKIP  [$1] $2"; }
warn()  { echo "WARN  [$1] $2"; }
info()  { echo "INFO  $*"; }
err()   { echo "ERROR [$1] $2" >&2; }
}

errors=0
checked=0

# Contamination signal: any TFM/RID composite key. Use POSIX ERE (-qE) so
# this works on macOS BSD grep without -P / PCRE.
contamination_re='"net[0-9]+\.[0-9]+/'

check_content() {
    # $1 = display name, $2 = file content (may be empty or binary)
    local name="$1" content="$2"
    if printf '%s' "$content" | grep -qE "$contamination_re"; then
        err "lock-shape" "$name contains RID-specific graph entries (net<tfm>/<rid>) — regenerate via 'dotnet restore' (no -r, no --force-evaluate) from src-sidecar/"
        ((errors++)) || true
        return 1
    fi
    ok "lock-shape" "$name"
    return 0
}

if [[ "$MODE" == "staged" ]]; then
    if ! git rev-parse --git-dir >/dev/null 2>&1; then
        err "preconditions" "not inside a git repository"
        exit 2
    fi

    while IFS= read -r file; do
        [[ "$file" == *packages.lock.json ]] || continue

        ((checked++)) || true
        # `git show :path` reads the staged version of a tracked file.
        if ! staged_content=$(git show ":$file" 2>/dev/null); then
            warn "lock-shape" "$file (could not read staged content)"
            continue
        fi
        check_content "$file" "$staged_content" || true
    done < <(git diff --cached --name-only --diff-filter=ACM)
else
    # Working-tree mode: scan all tracked + untracked lock files (excluding
    # build-output mirrors under bin/ and obj/).
    while IFS= read -r file; do
        [[ "$file" == *packages.lock.json ]] || continue
        ((checked++)) || true
        check_content "$file" "$(cat "$file" 2>/dev/null)" || true
    done < <(find . -name packages.lock.json -not -path '*/obj/*' -not -path '*/bin/*' 2>/dev/null | sed 's|^\./||')
fi

# Summary
echo "=================================="
if (( errors > 0 )); then
    echo "FAIL — $errors errors, 0 warnings ($checked file(s) checked)"
    exit 1
fi
if (( checked == 0 )); then
    echo "PASS — 0 errors, 0 warnings (no lock files found in scope)"
else
    echo "PASS — 0 errors, 0 warnings ($checked file(s) checked)"
fi
exit 0
