using System.Runtime.InteropServices;

namespace FingerTrap.Sidecar.Pty;

internal static partial class LinuxNativeMethods
{
    public const int O_RDWR = 2;
    public const int O_NOCTTY = 0x100;
    public const int F_SETFD = 2;
    public const int FD_CLOEXEC = 1;
    public const int POSIX_SPAWN_SETSID = 0x80;
    public const ulong TIOCSWINSZ = 0x5414;
    public const int WNOHANG = 1;

    // Allocate generously — glibc posix_spawnattr_t is ~336 bytes,
    // posix_spawn_file_actions_t is ~96 bytes; sizes vary by platform.
    public const int PosixSpawnattrSize = 1024;
    public const int PosixSpawnFileActionsSize = 1024;

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    [LibraryImport("libc", EntryPoint = "posix_openpt", SetLastError = true)]
    public static partial int PosixOpenpt(int flags);

    [LibraryImport("libc", EntryPoint = "grantpt", SetLastError = true)]
    public static partial int Grantpt(int fd);

    [LibraryImport("libc", EntryPoint = "unlockpt", SetLastError = true)]
    public static partial int Unlockpt(int fd);

    [LibraryImport("libc", EntryPoint = "ptsname_r", SetLastError = true)]
    public static partial int PtsnameR(int fd, [Out] byte[] buf, nuint buflen);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    public static partial int Fcntl(int fd, int cmd, int arg);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static partial int Ioctl(int fd, ulong request, ref WinSize ws);

    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    public static partial int Waitpid(int pid, out int status, int options);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    public static partial int Kill(int pid, int sig);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_init", SetLastError = true)]
    public static partial int PosixSpawnattrInit(nint attr);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_destroy", SetLastError = true)]
    public static partial int PosixSpawnattrDestroy(nint attr);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setflags", SetLastError = true)]
    public static partial int PosixSpawnattrSetflags(nint attr, short flags);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_init", SetLastError = true)]
    public static partial int PosixSpawnFileActionsInit(nint actions);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_destroy", SetLastError = true)]
    public static partial int PosixSpawnFileActionsDestroy(nint actions);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addopen", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int PosixSpawnFileActionsAddopen(nint actions, int fd, string path, int oflag, uint mode);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2", SetLastError = true)]
    public static partial int PosixSpawnFileActionsAdddup2(nint actions, int fd, int newfd);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addchdir_np", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int PosixSpawnFileActionsAddchdirNp(nint actions, string path);

    [LibraryImport("libc", EntryPoint = "posix_spawnp", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static unsafe partial int PosixSpawnp(out int pid, string file, nint actions, nint attr, byte** argv, byte** envp);

    public static int WExitStatus(int status) => (status >> 8) & 0xff;

    public static bool WIfExited(int status) => (status & 0x7f) == 0;

    public static int WTermSig(int status) => status & 0x7f;
}
