# 0007 — macOS PTY implementation: TIOCPTYGNAME, opaque-pointer spawn handles, zsh fallback

- Status: Superseded by [0008](0008-vendor-porta-pty.md)
- Date: 2026-05-05

## Context and problem statement

ADR-0006 established the PTY service shape (libc P/Invoke, base64 framing,
debounced resize) and identified macOS as the next target after Linux.
The Linux backend shipped at M1 (PR #4 + #5). Implementing the macOS
counterpart surfaced several Darwin-vs-glibc divergences that need to be
recorded so future contributors don't reintroduce them when adding
platforms or refactoring the PTY layer.

The divergences are not incidental — they are silent-failure traps. The
Linux constants for `O_NOCTTY`, `TIOCSWINSZ`, and `POSIX_SPAWN_SETSID` all
have different values on Darwin. Copying them verbatim would compile
cleanly and fail in subtle ways at runtime (no controlling terminal, ioctl
silently rejected, child spawned suspended). Likewise, `posix_spawnattr_t`
is an inline struct on glibc but an opaque pointer typedef on Darwin —
the same `Marshal.AllocHGlobal(N)` pattern that works on glibc corrupts
the spawn flow on macOS.

This ADR records the macOS-specific design choices, including the
intentional decision NOT to refactor the Linux and macOS backends into a
shared abstraction at this stage.

## Considered options

- **Mirror `LinuxPtyService` with a sibling `MacOsPtyService`** (this ADR).
  Two parallel concrete classes, each with its own `*NativeMethods` P/Invoke
  surface. Selected by `Program.CreatePtyService` via
  `RuntimeInformation.IsOSPlatform`. Duplication is real but the divergence
  surface (constants, ioctl signatures, spawn-handle ABI, ptsname strategy)
  is large enough that a shared abstraction would leak.

- **Generic `PosixPtyService<TNative>` parameterised by an INativeMethods
  strategy.** Rejected: the `posix_spawnattr_t` ABI difference forces
  different P/Invoke signatures (`out nint` vs `Marshal.AllocHGlobal`),
  which the strategy interface cannot hide cleanly. Five constants differ.
  `ptsname_r` becomes a `TIOCPTYGNAME` ioctl. The interface ends up wide
  enough that the abstraction cost exceeds the duplication cost.

- **Use `ptsname_r` on macOS.** Rejected. Apple added `ptsname_r` in macOS
  10.13.4. Our floor is macOS 11 so it is technically available, but
  research surfaced uncertainty across sources about its presence on every
  10.13.4+ system, and the function is not advertised in `<stdlib.h>` on
  some SDKs without the right `_DARWIN_C_SOURCE` setting. `TIOCPTYGNAME`
  is a Darwin BSD ioctl that has been stable since at least macOS 10.5
  with no version cliff.

- **Use `/bin/bash` as the macOS shell fallback (matching Linux).**
  Rejected. macOS ships bash 3.2 (frozen at GPL2) which is ancient and
  missing many modern features. Catalina (10.15) and later default user
  accounts to `/bin/zsh`. Falling back to `/bin/zsh → /bin/sh` better
  matches the platform default and avoids surprising the user.

## Decision outcome

Chosen option: **sibling `MacOsPtyService` mirroring `LinuxPtyService`**,
with the macOS-specific design choices below.

### Constant values (macOS)

| Constant | macOS value | Linux value | Notes |
|---|---|---|---|
| `O_RDWR` | `2` | `2` | Same |
| `O_NOCTTY` | `0x20000` | `0x100` | **Differs** |
| `F_SETFD` | `2` | `2` | Same |
| `FD_CLOEXEC` | `1` | `1` | Same |
| `EINTR` | `4` | `4` | Same (POSIX) |
| `WNOHANG` | `1` | `1` | Same (POSIX) |
| `POSIX_SPAWN_SETSID` | `0x0400` | `0x0080` | **Differs** |
| `TIOCSWINSZ` | `0x80087467` | `0x5414` | **Differs**; declared `uint` to avoid sign extension |
| `TIOCPTYGNAME` | `0x40807453` | n/a | macOS-only; replaces `ptsname_r` |

`WIFEXITED` / `WEXITSTATUS` / `WTERMSIG` bit-decoding macros are
POSIX-mandated and identical between platforms.

### Spawn-handle ABI

`posix_spawnattr_t` and `posix_spawn_file_actions_t` on Darwin are
`typedef void *` opaque pointer types. Every C function in the
`posix_spawn` family takes the handle as `T *attr` (e.g.
`posix_spawnattr_setflags(posix_spawnattr_t *attr, short flags)`),
which after typedef substitution resolves to `void **attr`. The xnu
implementation dereferences that pointer to extract the heap pointer
the matching `*_init` wrote, then operates on the underlying struct.

Therefore every P/Invoke that takes a handle parameter uses `ref nint`
(or `out nint` for init), not `nint`:

- `*_init(out nint)` — .NET passes a pointer to an `nint` slot; the
  C side `malloc`s the struct and writes the heap pointer into the slot.
- `*_destroy(ref nint)` — same ABI; C dereferences to free.
- Setters (`setflags`, `addopen`, `adddup2`, `addchdir_np`) — `ref nint`.
- `posix_spawnp(..., ref nint actions, ref nint attr, ...)` — same.

Passing the handle by value (`nint` instead of `ref nint`) compiles
cleanly and `posix_spawnp` returns 0, but the C side dereferences the
handle expecting `void **` and reads garbage from the first 8 bytes of
the underlying struct. The net effect is that no file actions are
registered: the spawned shell inherits the parent's stdin/stdout/stderr
instead of attaching to the slave PTY. This was the failure mode caught
during initial M2 Max validation. Linux's glibc uses an inline-struct
`posix_spawnattr_t` with the same `T *` calling convention, so the
Linux service's `nint` parameter happens to be correct on glibc — but
it is not portable to Darwin.

We do not allocate the 1024-byte buffer the Linux path uses, because
there is no inline struct to hold; the .NET side just owns the
pointer-sized slot.

### Slave path retrieval

We use `ioctl(masterFd, TIOCPTYGNAME, buf)` with a 128-byte buffer instead
of `ptsname_r`. The buffer is null-terminated by the kernel.

### Master read EOF

On Linux the kernel signals master EOF (slave fully closed) by returning
EIO, which `FileStream.ReadAsync` surfaces as `IOException`. On macOS the
kernel returns 0 (clean EOF). The read loop already handles `read == 0`
as terminal, so the macOS path takes that branch; the `IOException` catch
is retained as defensive coverage and is harmless on Darwin.

### Shell selection

`MacOsPtyService.ResolveShell` falls back through:

1. The `Shell` field on `PtySpawnOptions`, if set.
2. The `$SHELL` environment variable.
3. `/bin/zsh` if it exists.
4. `/bin/sh` otherwise.

This differs from `LinuxPtyService` which falls back to `/bin/bash`. On
macOS, `/bin/bash` is a 17-year-old bash 3.2 frozen for GPL reasons and
is not the platform default; `/bin/zsh` is the Catalina+ user default.

### Minimum macOS version

macOS 11 (Big Sur) or later. `posix_spawn_file_actions_addchdir_np`
requires 10.15+, so 11 is comfortably above the floor and covers all
Apple Silicon plus current Intel hardware.

### Code structure

`MacOsNativeMethods.cs` and `MacOsPtyService.cs` live next to the Linux
pair under `src-sidecar/src/FingerTrap.Sidecar/Pty/`. No shared base class
or generic abstraction. `Program.CreatePtyService` selects the
implementation by `RuntimeInformation.IsOSPlatform`. The
`UnsupportedPtyService` fallback now covers Windows only and references
both ADR-0006 and this ADR.

A future, narrower refactor may extract `NativeStringArray` and
`BuildEnvp` into a shared `PosixPtyHelpers` static class — those are pure
C# with no platform dependency. Tracked as a follow-up; not in scope here.

### Windows

Remains deferred per ADR-0006 §5. This ADR does not change that stance.
A Windows ConPty backend would not share the P/Invoke surface with either
POSIX implementation; when (or if) it lands, it gets its own sibling
class.

## Consequences

- Good: macOS now has a working local PTY backend. M1 closes for both
  fully supported platforms.
- Good: Each `*NativeMethods` file is a single source of truth for the
  platform's syscall ABI. Adding a constant doesn't risk perturbing the
  other platform.
- Good: Recording the constant divergences here means a future Windows
  contributor (or a refactor) won't reintroduce them by accident.
- Bad: Real duplication between `LinuxPtyService` and `MacOsPtyService`
  (~80% of method bodies are structurally identical). Accepted for now;
  follow-up may extract shared helpers if duplication grows.
- Neutral: macOS validation requires real hardware. CI compiles the
  sidecar on macos-15 but does not run interactive PTY tests. Initial
  validation is on a developer M2 Max; CI catches regressions only at
  the compile/link layer.
