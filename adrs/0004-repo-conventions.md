# 0004 — Repo conventions: GitHub Flow, Conventional Commits, SemVer, MADR ADRs

- Status: Accepted
- Date: 2026-04-26

## Context and problem statement

A solo project still benefits from explicit conventions: future-me reads PRs,
release notes, and ADRs months later and needs them to be consistent. CI
enforcement (PR title linting, `scripts/check.sh`) needs a stable target.

## Considered options

- **Trunk-based development** with no integration branch. Rejected; the
  project will eventually have multiple in-flight features and a stable
  release branch.
- **GitFlow with `develop`, `release/*`, `hotfix/*` branches**. Rejected;
  too much branch ceremony for a project that releases as a desktop app.
- **GitHub Flow with `dev` as the integration branch and `main` as stable**
  (this ADR). Short-lived feature branches into `dev`; `dev` promotes to
  `main` via merge commit; tags cut from `main`.
- Free-form commit messages. Rejected; release-note generation and
  PR-title linting both want structure.

## Decision outcome

### Branching

GitHub Flow with `dev` as the integration branch:

- Feature branches: `<type>/kebab-case-description`, branched off `dev`,
  short-lived (target ≤3 days).
- `dev` is the integration branch. PRs target `dev`, **squash merge**.
- `main` is the stable branch. `dev` → `main` PRs use **merge commit**
  (preserves squashed SHAs so subsequent promotions don't conflict).
- Branches deleted after merge.
- No `hotfix/`, `release/`, or `dev/` prefixes.

### Commits and PR titles

Conventional Commits: `<type>(<scope>): <description>`.

- Types: `feat`, `fix`, `docs`, `chore`, `refactor`, `test`, `ci`, `style`.
- Description: imperative, lowercase, no trailing period.
- Breaking change: `!` after type/scope.
- No authorship trailers.

PR titles must be valid Conventional Commits messages — they become the
squash-merge commit message on `dev`. Enforced by
`amannn/action-semantic-pull-request` against PRs targeting `dev`.

### Versioning

SemVer with a `v` prefix, annotated tags, cut from `main` only:

- `feat` → MINOR
- `fix`, `perf` → PATCH
- `BREAKING CHANGE` or `!` → MAJOR (post-1.0); MINOR pre-1.0
- `docs`, `chore`, `style`, `refactor`, `test`, `ci` → no bump

Pre-1.0 (we start at `v0.1.0`) breaking changes bump MINOR.

### ADRs

MADR minimal format in `adrs/`. Sequential zero-padded three digits, never
reused. Supersession over editing — to revise a prior decision, mark the
original as superseded and write a new ADR.

### Script output

All scripts use 6-character labels (`OK`, `SKIP`, `WARN`, `INFO`, `ERROR`),
end multi-check runs with a `===` summary block, and exit `0` for pass,
`1` for errors, `2` for precondition failure. `set -euo pipefail` on every
new shell script.

## Considered options for promotion merge strategy

- **Squash merge promotions to `main`.** Rejected — produces new SHAs on
  `main` that don't match `dev`'s, causing conflicts on every subsequent
  promotion.
- **Rebase merge.** Rejected — same divergence problem, plus rewrites SHAs.
- **Merge commit promotions.** Chosen. Preserves shared SHAs.

### Consequences

- Good: every artifact (commits, tags, ADRs, PRs) is structurally
  predictable.
- Good: `semantic-release` can derive versions from commit history when we
  add release automation.
- Bad: the convention adds friction on first-time contributors. Mitigated
  by enforcing PR-title format in CI.
- Neutral: squash-vs-merge promotion strategy is unintuitive but
  documented here and in the GitHub repo settings.
