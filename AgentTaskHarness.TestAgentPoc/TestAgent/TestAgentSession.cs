using AgentTaskHarness.TestAgentPoc.Pty;

namespace AgentTaskHarness.TestAgentPoc.TestAgent;

/// <summary>
/// Status snapshot sent to clients via SignalR and HTTP endpoints.
/// </summary>
public sealed record TestAgentStatusDto(
    string SessionId,
    string State,
    string? CurrentToolName,
    string? CurrentToolArgs,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityTimestamp,
    bool IsRunning,
    int? ExitCode,
    string? FailureReason);

/// <summary>
/// Holds the runtime state, PTY process reference, and hook metadata for an interactive test agent session.
/// </summary>
public sealed class TestAgentSession
{
    private readonly object _lock = new();

    public Guid Id { get; }
    public string TargetDirectory { get; }
    public string Requirements { get; }
    public bool IsShell { get; }
    public DateTimeOffset CreatedAt { get; }

    public TestAgentState State { get; private set; }
    public string? CurrentToolName { get; private set; }
    public string? CurrentToolArgs { get; private set; }
    public DateTimeOffset LastActivityTimestamp { get; private set; }
    public int? ExitCode { get; private set; }
    public string? FailureReason { get; private set; }
    public string? TempHooksDir { get; set; }

    public IPtySession? PtySession { get; set; }

    public event Action<TestAgentSession, TestAgentState>? StateChanged;

    public TestAgentSession(
        Guid id,
        string targetDirectory,
        string requirements,
        bool isShell = false,
        TestAgentState initialState = TestAgentState.Idle)
    {
        Id = id;
        TargetDirectory = targetDirectory;
        Requirements = requirements;
        IsShell = isShell;
        CreatedAt = DateTimeOffset.UtcNow;
        LastActivityTimestamp = CreatedAt;
        State = initialState;
    }

    public void TouchActivity()
    {
        lock (_lock)
        {
            LastActivityTimestamp = DateTimeOffset.UtcNow;
        }
    }

    public bool TransitionState(
        TestAgentState newState,
        string? toolName = null,
        string? toolArgs = null,
        string? failureReason = null,
        int? exitCode = null)
    {
        TestAgentState oldState;
        lock (_lock)
        {
            if (State == newState &&
                CurrentToolName == toolName &&
                CurrentToolArgs == toolArgs &&
                FailureReason == failureReason &&
                ExitCode == exitCode)
            {
                return false;
            }

            oldState = State;
            State = newState;
            CurrentToolName = toolName;
            CurrentToolArgs = toolArgs;
            if (failureReason != null)
            {
                FailureReason = failureReason;
            }
            if (exitCode.HasValue)
            {
                ExitCode = exitCode;
            }
            LastActivityTimestamp = DateTimeOffset.UtcNow;
        }

        StateChanged?.Invoke(this, oldState);
        return true;
    }

    public TestAgentStatusDto ToStatusDto()
    {
        lock (_lock)
        {
            bool isRunning = State is not (TestAgentState.Finished or TestAgentState.Failed or TestAgentState.Idle);
            return new TestAgentStatusDto(
                SessionId: Id.ToString(),
                State: State.ToString(),
                CurrentToolName: CurrentToolName,
                CurrentToolArgs: CurrentToolArgs,
                CreatedAt: CreatedAt,
                LastActivityTimestamp: LastActivityTimestamp,
                IsRunning: isRunning,
                ExitCode: ExitCode,
                FailureReason: FailureReason);
        }
    }
}
