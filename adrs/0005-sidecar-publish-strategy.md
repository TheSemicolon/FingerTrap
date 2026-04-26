# 0005 — Sidecar publish strategy: framework-dependent self-contained, untrimmed at M0

- Status: Accepted
- Date: 2026-04-26

## Context and problem statement

The .NET sidecar (ADR-0001) ships as a Tauri `externalBin` — a per-RID
binary bundled with the Tauri installer. We need to choose between
framework-dependent and self-contained publishes, and decide whether to
trim or AOT-compile.

`StreamJsonRpc` 2.24.84 is the wire protocol library (ADR-0002). Its default
`JsonMessageFormatter` is annotated `[RequiresUnreferencedCode]` and
`[RequiresDynamicCode]`. With our `TreatWarningsAsErrors` stance,
`PublishTrimmed=true` or `PublishAot=true` produces IL2026/IL3050 build
errors.

## Considered options

- **Framework-dependent** — sidecar binary requires the .NET 10 runtime to
  be installed on the user's machine. Smallest binary; worst install UX
  (users on macOS/Windows typically don't have .NET 10).
- **Self-contained, untrimmed** (this ADR) — bundles the runtime per RID.
  ~60–80 MB per RID. Largest binary; best install UX (no prerequisite).
- **Self-contained, trimmed** — smaller bundle but breaks our build under
  `JsonMessageFormatter`. Would require switching to `SystemTextJsonFormatter`
  with a source-generated `JsonSerializerContext` covering every RPC
  parameter and return type.
- **NativeAOT** — best startup, smallest disk footprint, same trim
  constraints as above plus loses dynamic proxy generation that
  `StreamJsonRpc` uses for `AddLocalRpcTarget`. Available, not viable today.

## Decision outcome

Chosen option: **self-contained, untrimmed.**

For M0–M7 the sidecar publishes per RID with `--self-contained true` and
no trimming or AOT. Targets:

- `osx-arm64`
- `osx-x64`
- `linux-x64`
- `linux-arm64`
- `win-x64`

The published binary is named `fingertrap-sidecar-<rust-target-triple>[.exe]`
and is staged into `src-tauri/binaries/` before `tauri build`.

Trimming and AOT are revisited at M8 (packaging) when the RPC surface is
stable enough to enumerate every serialized type in a
`JsonSerializerContext`. At that point a successor ADR documents the switch
to `SystemTextJsonFormatter` plus source-generated context.

### Consequences

- Good: sidecar starts with no runtime prerequisite for the user.
- Good: `[RequiresDynamicCode]`/`[RequiresUnreferencedCode]` warnings stay
  at `Warning` rather than blocking the build.
- Good: no source-gen context to maintain at M0.
- Bad: ~60–80 MB per-RID bundle. Multiplied across five RIDs in the
  release matrix, the download story for distribution is not free.
- Bad: M8 has a non-trivial trim/AOT migration ahead of it.
- Neutral: install-time prerequisites would have been worse for a personal
  desktop app aimed at non-developer machines.
