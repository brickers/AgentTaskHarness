namespace AgentTaskHarness.TestAgentPoc.Pty;

/// <summary>
/// Configuration for starting a pseudo-terminal process session.
/// </summary>
public sealed record PtyStartInfo(
    string Command,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    int InitialCols = 80,
    int InitialRows = 24);

/// <summary>
/// Represents an active POSIX pseudo-terminal session.
/// </summary>
public interface IPtySession : IAsyncDisposable, IDisposable
{
    Guid Id { get; }
    int Pid { get; }
    bool IsRunning { get; }
    int? ExitCode { get; }
    Stream MasterStream { get; }

    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
    ValueTask WriteAsync(string text, CancellationToken cancellationToken = default);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    void Resize(int cols, int rows);
    void Kill(int signal = NativePosixPty.SIGTERM);
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);
    void StartOutputPump(Func<byte[], CancellationToken, Task> onData, Action<int>? onExit = null);
}

/// <summary>
/// Service interface to start and manage PTY sessions.
/// </summary>
public interface IPtyService
{
    IPtySession StartSession(PtyStartInfo startInfo);
    IPtySession StartCopilotSession(string targetDir, string prompt, int cols = 80, int rows = 24, IReadOnlyDictionary<string, string>? environmentVariables = null);
}
