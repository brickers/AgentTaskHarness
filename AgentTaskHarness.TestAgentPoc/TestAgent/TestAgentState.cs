namespace AgentTaskHarness.TestAgentPoc.TestAgent;

/// <summary>
/// Lifecycle states for an interactive Copilot test agent session.
/// </summary>
public enum TestAgentState
{
    /// <summary>No session is currently active or session has been reset.</summary>
    Idle,

    /// <summary>Copilot CLI process is starting up or hooks are being configured.</summary>
    Initializing,

    /// <summary>Agent is processing instructions, user turn, or determining next step.</summary>
    Thinking,

    /// <summary>Agent is actively executing a tool (e.g. bash, file edit, search).</summary>
    ToolExecuting,

    /// <summary>Agent has requested execution of a sensitive tool and is waiting for user approval.</summary>
    WaitingForApproval,

    /// <summary>Agent is waiting for interactive chat input or clarification via ask_user.</summary>
    WaitingForUser,

    /// <summary>Agent has not produced output or hook events for the configured watchdog period.</summary>
    Stalled,

    /// <summary>Agent encountered an uncaught error, failed hook, or exited abnormally.</summary>
    Failed,

    /// <summary>Agent has cleanly completed its tasks or closed the session.</summary>
    Finished
}
