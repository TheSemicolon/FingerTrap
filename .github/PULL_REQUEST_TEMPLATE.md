## Summary

<!-- 2-4 sentences. The "why" matters more than the "what" — the diff covers what. -->

## Type of Change

- [ ] `feat` — new feature
- [ ] `fix` — bug fix
- [ ] `docs` — documentation only
- [ ] `chore` — maintenance
- [ ] `refactor` — restructuring without behavior change
- [ ] `test` — adding or updating tests
- [ ] `ci` — CI/CD changes
- [ ] `style` — formatting or linting fixes
- [ ] Breaking change (add `!` to the PR title)

## Test Plan

<!--
What did you do to verify the change works? Include CI status, local test
output, or "no testable behavior changed" for docs/config-only PRs.
-->

## Checklist

- [ ] PR title is a valid Conventional Commits message: `<type>(<scope>): <description>`.
- [ ] No secrets, credentials, or environment-specific config committed.
- [ ] If `RpcSurface.cs` changed, the matching surface in `src-ui/src/api.ts` changed in this PR (ADR-0003).
- [ ] If a convention, architecture, or technology choice was introduced or revised, an ADR landed in `adrs/`.
- [ ] CI is green on Windows, macOS, and Linux.
- [ ] `scripts/check.sh` passes locally.
