using System.Runtime.InteropServices;

namespace AgentTaskHarness.TestAgentPoc.Pty;

/// <summary>
/// Native P/Invoke signatures for POSIX pseudo-terminal (PTY) management and process lifecycle
/// on macOS (Darwin) and compatible Unix systems.
/// </summary>
public static class NativePosixPty
{
    private const string LibUtil = "libutil";
    private const string LibC = "libc";

    // Signals
    public const int SIGHUP = 1;
    public const int SIGINT = 2;
    public const int SIGQUIT = 3;
    public const int SIGKILL = 9;
    public const int SIGTERM = 15;

    // waitpid options
    public const int WNOHANG = 1;
    public const int WUNTRACED = 2;

    // ioctl terminal window size request codes
    // On macOS: _IOW('t', 103, struct winsize) => 0x80087467
    public const ulong TIOCSWINSZ = 0x80087467UL;
    // On macOS: _IOR('t', 104, struct winsize) => 0x40087468
    public const ulong TIOCGWINSZ = 0x40087468UL;

    [StructLayout(LayoutKind.Sequential)]
    public struct Winsize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    /// <summary>
    /// Allocates a pseudo-terminal pair (master and slave).
    /// </summary>
    [DllImport(LibUtil, SetLastError = true)]
    public static extern int openpty(
        out int amaster,
        out int aslave,
        IntPtr name,
        IntPtr termp,
        ref Winsize winp);

    [DllImport(LibUtil, SetLastError = true)]
    public static extern int openpty(
        out int amaster,
        out int aslave,
        IntPtr name,
        IntPtr termp,
        IntPtr winp);

    /// <summary>
    /// Initializes spawn file actions object.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawn_file_actions_init(out IntPtr fileActions);

    /// <summary>
    /// Destroys spawn file actions object.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawn_file_actions_destroy(ref IntPtr fileActions);

    /// <summary>
    /// Adds a dup2 action to file actions (duplicate filedes to newfiledes).
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawn_file_actions_adddup2(ref IntPtr fileActions, int filedes, int newfiledes);

    /// <summary>
    /// Adds a close action to file actions.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawn_file_actions_addclose(ref IntPtr fileActions, int filedes);

    /// <summary>
    /// Non-portable macOS extension: changes current working directory for spawned process.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawn_file_actions_addchdir_np(ref IntPtr fileActions, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    /// <summary>
    /// Spawns a new process searching PATH for the file.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int posix_spawnp(
        out int pid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string file,
        in IntPtr fileActions,
        in IntPtr attrp,
        IntPtr[] argv,
        IntPtr[] envp);

    /// <summary>
    /// Control device request (e.g. setting window size).
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int ioctl(int fd, ulong request, ref Winsize ws);

    /// <summary>
    /// Sends a signal to a process.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int kill(int pid, int sig);

    /// <summary>
    /// Waits for child process state change.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int waitpid(int pid, out int stat_loc, int options);

    /// <summary>
    /// Closes a file descriptor.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int close(int fd);

    /// <summary>
    /// Duplicates an open file descriptor.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    public static extern int dup(int fd);

    // POSIX status inspection macros
    public static bool WIFEXITED(int status) => (status & 0x7F) == 0;
    public static int WEXITSTATUS(int status) => (status >> 8) & 0xFF;
    public static bool WIFSIGNALED(int status) => ((status & 0x7F) != 0) && ((status & 0x7F) != 0x7F);
    public static int WTERMSIG(int status) => status & 0x7F;
}
