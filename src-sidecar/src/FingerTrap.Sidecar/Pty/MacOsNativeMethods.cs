using System.Runtime.InteropServices;

namespace FingerTrap.Sidecar.Pty;

internal static partial class MacOsNativeMethods
{
    public const int O_RDWR = 2;

    // macOS O_NOCTTY differs from Linux (0x100) — copying the Linux value
    // would silently fail to suppress controlling-terminal acquisition.
    public const int O_NOCTTY = 0x20000;

    public const int F_SETFD = 2;
    public const int FD_CLOEXEC = 1;

    // macOS POSIX_SPAWN_SETSID (0x0400) ≠ Linux (0x80). Using the Linux value
    // here would set POSIX_SPAWN_START_SUSPENDED instead and hang the shell.
    public const short POSIX_SPAWN_SETSID = 0x0400;

    // macOS ioctl encoding: _IOW('t', 103, struct winsize) = 0x80087467.
    // High bit set, so this MUST be uint (not int / long) to avoid sign
    // extension when the C side reads `unsigned long request`.
    public const uint TIOCSWINSZ = 0x80087467u;

    // _IOR('t', 83, [128]char) — macOS ioctl that copies the slave PTY path
    // into the caller's buffer. Used in lieu of ptsname_r (which has a
    // 10.13.4 availability cliff). Buffer must be at least 128 bytes.
    public const uint TIOCPTYGNAME = 0x40807453u;
    public const int TIOCPTYGNAME_BUFLEN = 128;

    public const int WNOHANG = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    // libc on macOS resolves through the dyld shared cache to libSystem.
    // From macOS 11+ the .dylib files are no longer on disk; resolution still
    // works via the cache. "libc" is portable across osx-arm64 and osx-x64.
    private const string Libc = "libc";

    [LibraryImport(Libc, EntryPoint = "posix_openpt", SetLastError = true)]
    public static partial int PosixOpenpt(int flags);

    [LibraryImport(Libc, EntryPoint = "grantpt", SetLastError = true)]
    public static partial int Grantpt(int fd);

    [LibraryImport(Libc, EntryPoint = "unlockpt", SetLastError = true)]
    public static partial int Unlockpt(int fd);

    [LibraryImport(Libc, EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [LibraryImport(Libc, EntryPoint = "write", SetLastError = true)]
    public static unsafe partial nint Write(int fd, byte* buf, nuint count);

    [LibraryImport(Libc, EntryPoint = "fcntl", SetLastError = true)]
    public static partial int Fcntl(int fd, int cmd, int arg);

    // request is uint to keep TIOCSWINSZ / TIOCPTYGNAME from sign-extending.
    [LibraryImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    public static partial int IoctlWinSize(int fd, uint request, ref WinSize ws);

    [LibraryImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    public static partial int IoctlBuffer(int fd, uint request, [Out] byte[] buf);

    [LibraryImport(Libc, EntryPoint = "waitpid", SetLastError = true)]
    public static partial int Waitpid(int pid, out int status, int options);

    [LibraryImport(Libc, EntryPoint = "kill", SetLastError = true)]
    public static partial int Kill(int pid, int sig);

    // posix_spawnattr_t is `typedef void *posix_spawnattr_t` on macOS — an
    // opaque pointer. Init malloc()s the underlying struct and writes the
    // heap pointer into *attr. Use `out nint` so the P/Invoke layer passes a
    // pointer to a single nint slot the syscall fills in. All subsequent
    // calls take the handle value (nint), not a pointer-to-handle.
    [LibraryImport(Libc, EntryPoint = "posix_spawnattr_init", SetLastError = true)]
    public static partial int PosixSpawnattrInit(out nint attr);

    [LibraryImport(Libc, EntryPoint = "posix_spawnattr_destroy", SetLastError = true)]
    public static partial int PosixSpawnattrDestroy(ref nint attr);

    // C signatures take posix_spawnattr_t * / posix_spawn_file_actions_t *,
    // and on Darwin those typedefs are `void *`. So `T *` resolves to
    // `void **` at the ABI — pointer-to-handle, not handle-by-value. Each
    // setter dereferences the slot to extract the malloc'd struct pointer
    // that init() wrote. `ref nint` matches that ABI; `nint` would pass
    // the handle by value and cause silent garbage dereferences that look
    // like spawn-returned-0 with no file actions actually applied.
    [LibraryImport(Libc, EntryPoint = "posix_spawnattr_setflags", SetLastError = true)]
    public static partial int PosixSpawnattrSetflags(ref nint attr, short flags);

    [LibraryImport(Libc, EntryPoint = "posix_spawn_file_actions_init", SetLastError = true)]
    public static partial int PosixSpawnFileActionsInit(out nint actions);

    [LibraryImport(Libc, EntryPoint = "posix_spawn_file_actions_destroy", SetLastError = true)]
    public static partial int PosixSpawnFileActionsDestroy(ref nint actions);

    [LibraryImport(Libc, EntryPoint = "posix_spawn_file_actions_addopen", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int PosixSpawnFileActionsAddopen(ref nint actions, int fd, string path, int oflag, uint mode);

    [LibraryImport(Libc, EntryPoint = "posix_spawn_file_actions_adddup2", SetLastError = true)]
    public static partial int PosixSpawnFileActionsAdddup2(ref nint actions, int fd, int newfd);

    // posix_spawn_file_actions_addchdir_np was added in macOS 10.15. We
    // require macOS 11+, so this is safe.
    [LibraryImport(Libc, EntryPoint = "posix_spawn_file_actions_addchdir_np", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int PosixSpawnFileActionsAddchdirNp(ref nint actions, string path);

    [LibraryImport(Libc, EntryPoint = "posix_spawnp", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static unsafe partial int PosixSpawnp(out int pid, string file, ref nint actions, ref nint attr, byte** argv, byte** envp);

    // Status decoding macros are POSIX-mandated and identical to Linux.
    public static int WExitStatus(int status) => (status >> 8) & 0xff;

    public static bool WIfExited(int status) => (status & 0x7f) == 0;

    public static int WTermSig(int status) => status & 0x7f;
}
