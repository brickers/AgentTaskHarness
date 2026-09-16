using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentTaskHarness.TestAgentPoc.Pty;

/// <summary>
/// Encapsulates a POSIX pseudo-terminal session, managing child process lifecycle,
/// bidirectional master I/O streams, terminal window resizing, and asynchronous output pumping.
/// </summary>
public sealed class PtySession : IPtySession
{
    private readonly SafeFileHandle _readHandle;
    private readonly SafeFileHandle _writeHandle;
    private readonly FileStream _readStream;
    private readonly FileStream _writeStream;
    private readonly TaskCompletionSource<int> _exitCodeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _pumpCts = new();
    private Task? _monitorTask;
    private Task? _pumpTask;
    private int _disposed;
    private int? _cachedExitCode;

    public Guid Id { get; } = Guid.NewGuid();
    public int Pid { get; }
    public bool IsRunning => _cachedExitCode == null && !_exitCodeTcs.Task.IsCompleted;
    public int? ExitCode => _cachedExitCode;
    public Stream MasterStream => _readStream;

    private PtySession(int pid, int masterFd)
    {
        Pid = pid;

        int writeFd = NativePosixPty.dup(masterFd);
        if (writeFd < 0)
        {
            NativePosixPty.close(masterFd);
            throw new InvalidOperationException($"dup failed on masterFd: {Marshal.GetLastPInvokeError()}");
        }

        _readHandle = new SafeFileHandle((nint)masterFd, ownsHandle: true);
        _writeHandle = new SafeFileHandle((nint)writeFd, ownsHandle: true);

        // Under POSIX character devices (PTYs), the descriptor is synchronous;
        // setting isAsync: false allows FileStream to bind to the native handle and execute async I/O via threadpool.
        _readStream = new FileStream(_readHandle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        _writeStream = new FileStream(_writeHandle, FileAccess.Write, bufferSize: 4096, isAsync: false);

        StartProcessMonitor();
    }

    /// <summary>
    /// Spawns a new process inside a newly allocated POSIX pseudo-terminal.
    /// </summary>
    public static PtySession Start(PtyStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(startInfo.Command);

        var ws = new NativePosixPty.Winsize
        {
            ws_col = (ushort)Math.Max(1, startInfo.InitialCols),
            ws_row = (ushort)Math.Max(1, startInfo.InitialRows),
            ws_xpixel = 0,
            ws_ypixel = 0
        };

        int openPtyResult = NativePosixPty.openpty(out int masterFd, out int slaveFd, IntPtr.Zero, IntPtr.Zero, ref ws);
        if (openPtyResult != 0)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"openpty failed with error {err}");
        }

        IntPtr actions = IntPtr.Zero;
        int actionInitErr = NativePosixPty.posix_spawn_file_actions_init(out actions);
        if (actionInitErr != 0)
        {
            NativePosixPty.close(masterFd);
            NativePosixPty.close(slaveFd);
            throw new InvalidOperationException($"posix_spawn_file_actions_init failed with error {actionInitErr}");
        }

        var allocatedArgPtrs = new List<IntPtr>();
        var allocatedEnvPtrs = new List<IntPtr>();

        try
        {
            NativePosixPty.posix_spawn_file_actions_adddup2(ref actions, slaveFd, 0);
            NativePosixPty.posix_spawn_file_actions_adddup2(ref actions, slaveFd, 1);
            NativePosixPty.posix_spawn_file_actions_adddup2(ref actions, slaveFd, 2);
            NativePosixPty.posix_spawn_file_actions_addclose(ref actions, masterFd);
            NativePosixPty.posix_spawn_file_actions_addclose(ref actions, slaveFd);

            if (!string.IsNullOrEmpty(startInfo.WorkingDirectory) && Directory.Exists(startInfo.WorkingDirectory))
            {
                try
                {
                    NativePosixPty.posix_spawn_file_actions_addchdir_np(ref actions, startInfo.WorkingDirectory);
                }
                catch (EntryPointNotFoundException)
                {
                    // Fallback for systems lacking addchdir_np
                }
            }

            // Build argv: argv[0] = Command, followed by arguments, terminated with NULL pointer
            var argvList = new List<string> { startInfo.Command };
            if (startInfo.Arguments != null)
            {
                argvList.AddRange(startInfo.Arguments);
            }

            var argvArray = new IntPtr[argvList.Count + 1];
            for (int i = 0; i < argvList.Count; i++)
            {
                IntPtr ptr = Marshal.StringToCoTaskMemUTF8(argvList[i]);
                allocatedArgPtrs.Add(ptr);
                argvArray[i] = ptr;
            }
            argvArray[argvList.Count] = IntPtr.Zero;

            // Build envp: inherit environment + add custom vars + terminal configs
            var envDict = new Dictionary<string, string>();
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string k && entry.Value is string v)
                {
                    envDict[k] = v;
                }
            }

            // Ensure standard interactive terminal environment variables
            envDict["TERM"] = "xterm-256color";
            envDict["COLORTERM"] = "truecolor";

            // Prepend common Homebrew and local binary paths if missing
            string currentPath = envDict.TryGetValue("PATH", out string? p) ? p : "";
            if (!currentPath.Contains("/opt/homebrew/bin"))
            {
                envDict["PATH"] = $"/opt/homebrew/bin:/usr/local/bin:{currentPath}";
            }

            if (startInfo.EnvironmentVariables != null)
            {
                foreach (var kvp in startInfo.EnvironmentVariables)
                {
                    envDict[kvp.Key] = kvp.Value;
                }
            }

            var envStrings = envDict.Select(kv => $"{kv.Key}={kv.Value}").ToList();
            var envArray = new IntPtr[envStrings.Count + 1];
            for (int i = 0; i < envStrings.Count; i++)
            {
                IntPtr ptr = Marshal.StringToCoTaskMemUTF8(envStrings[i]);
                allocatedEnvPtrs.Add(ptr);
                envArray[i] = ptr;
            }
            envArray[envStrings.Count] = IntPtr.Zero;

            IntPtr attrp = IntPtr.Zero;
            int spawnErr = NativePosixPty.posix_spawnp(
                out int pid,
                startInfo.Command,
                in actions,
                in attrp,
                argvArray,
                envArray);

            if (spawnErr != 0)
            {
                NativePosixPty.close(masterFd);
                NativePosixPty.close(slaveFd);
                throw new InvalidOperationException($"posix_spawnp failed for '{startInfo.Command}' with error code {spawnErr}");
            }

            // Parent process must close its copy of the slave descriptor so EOF propagates on child termination
            NativePosixPty.close(slaveFd);

            return new PtySession(pid, masterFd);
        }
        finally
        {
            foreach (IntPtr ptr in allocatedArgPtrs)
            {
                Marshal.FreeCoTaskMem(ptr);
            }
            foreach (IntPtr ptr in allocatedEnvPtrs)
            {
                Marshal.FreeCoTaskMem(ptr);
            }
            NativePosixPty.posix_spawn_file_actions_destroy(ref actions);
        }
    }

    /// <summary>
    /// Helper to locate the Copilot executable and spawn a copilot interactive session.
    /// </summary>
    public static PtySession StartCopilot(
        string targetDir,
        string prompt,
        int initialCols = 80,
        int initialRows = 24,
        IReadOnlyDictionary<string, string>? envVars = null)
    {
        string copilotBin = ResolveCopilotBinaryPath();

        var args = new List<string>
        {
            "-i",
            prompt,
            "-C",
            targetDir
        };

        var startInfo = new PtyStartInfo(
            Command: copilotBin,
            Arguments: args,
            WorkingDirectory: targetDir,
            EnvironmentVariables: envVars,
            InitialCols: initialCols,
            InitialRows: initialRows);

        return Start(startInfo);
    }

    public static string ResolveCopilotBinaryPath()
    {
        const string brewPath = "/opt/homebrew/bin/copilot";
        if (File.Exists(brewPath))
        {
            return brewPath;
        }

        const string usrLocalPath = "/usr/local/bin/copilot";
        if (File.Exists(usrLocalPath))
        {
            return usrLocalPath;
        }

        return "copilot";
    }

    private void StartProcessMonitor()
    {
        _monitorTask = Task.Factory.StartNew(() =>
        {
            int status = 0;
            int waitResult = NativePosixPty.waitpid(Pid, out status, 0);
            int exitCode;

            if (waitResult == Pid)
            {
                if (NativePosixPty.WIFEXITED(status))
                {
                    exitCode = NativePosixPty.WEXITSTATUS(status);
                }
                else if (NativePosixPty.WIFSIGNALED(status))
                {
                    exitCode = 128 + NativePosixPty.WTERMSIG(status);
                }
                else
                {
                    exitCode = status;
                }
            }
            else
            {
                exitCode = -1;
            }

            _cachedExitCode = exitCode;
            _exitCodeTcs.TrySetResult(exitCode);
        }, TaskCreationOptions.LongRunning);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _writeStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await _writeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return WriteAsync(bytes, cancellationToken);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            return await _readStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Master PTY stream closed by OS after child exit
            return 0;
        }
    }

    public void Resize(int cols, int rows)
    {
        ThrowIfDisposed();
        var ws = new NativePosixPty.Winsize
        {
            ws_col = (ushort)Math.Max(1, cols),
            ws_row = (ushort)Math.Max(1, rows),
            ws_xpixel = 0,
            ws_ypixel = 0
        };

        if (!_readHandle.IsClosed && !_readHandle.IsInvalid)
        {
            NativePosixPty.ioctl((int)_readHandle.DangerousGetHandle(), NativePosixPty.TIOCSWINSZ, ref ws);
        }
    }

    public void Kill(int signal = NativePosixPty.SIGTERM)
    {
        if (Pid > 0 && IsRunning)
        {
            NativePosixPty.kill(Pid, signal);
        }
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.CanBeCanceled)
        {
            return _exitCodeTcs.Task.WaitAsync(cancellationToken);
        }
        return _exitCodeTcs.Task;
    }

    public void StartOutputPump(Func<byte[], CancellationToken, Task> onData, Action<int>? onExit = null)
    {
        ArgumentNullException.ThrowIfNull(onData);
        ThrowIfDisposed();

        _pumpTask = Task.Run(async () =>
        {
            byte[] buffer = new byte[4096];
            try
            {
                while (!_pumpCts.IsCancellationRequested && !_readHandle.IsClosed)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await _readStream.ReadAsync(buffer.AsMemory(0, buffer.Length), _pumpCts.Token).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // Master PTY returned EOF/EIO on process close
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    byte[] chunk = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
                    await onData(chunk, _pumpCts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Suppress pump exit errors
            }

            int exitCode = await WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            onExit?.Invoke(exitCode);
        });
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _pumpCts.Cancel();
        }
        catch
        {
            // Ignored
        }

        if (IsRunning)
        {
            try
            {
                NativePosixPty.kill(Pid, NativePosixPty.SIGTERM);
            }
            catch
            {
                // Ignored
            }
        }

        _readStream.Dispose();
        _writeStream.Dispose();
        _readHandle.Dispose();
        _writeHandle.Dispose();
        _pumpCts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _pumpCts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignored
        }

        if (IsRunning)
        {
            try
            {
                NativePosixPty.kill(Pid, NativePosixPty.SIGTERM);
            }
            catch
            {
                // Ignored
            }
        }

        await _readStream.DisposeAsync().ConfigureAwait(false);
        await _writeStream.DisposeAsync().ConfigureAwait(false);
        _readHandle.Dispose();
        _writeHandle.Dispose();
        _pumpCts.Dispose();
    }
}
