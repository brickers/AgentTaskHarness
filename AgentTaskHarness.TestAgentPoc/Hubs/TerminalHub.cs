using AgentTaskHarness.TestAgentPoc.TestAgent;
using Microsoft.AspNetCore.SignalR;

namespace AgentTaskHarness.TestAgentPoc.Hubs;

/// <summary>
/// SignalR Hub for streaming interactive terminal I/O and session state between the browser and backend PTY.
/// </summary>
public sealed class TerminalHub : Hub
{
    private readonly TestAgentSessionManager _sessionManager;
    private readonly ILogger<TerminalHub> _logger;

    public TerminalHub(TestAgentSessionManager sessionManager, ILogger<TerminalHub> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// Attaches the client to a session's SignalR group and returns the current session status.
    /// </summary>
    public async Task<TestAgentStatusDto?> JoinSession(string sessionId)
    {
        if (Guid.TryParse(sessionId, out var guid))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, guid.ToString());
            var session = _sessionManager.GetSession(guid);
            return session?.ToStatusDto();
        }
        return null;
    }

    /// <summary>
    /// Starts an interactive Copilot agent session with optional task requirements.
    /// </summary>
    public async Task<string> StartSession(string targetDir, string requirements, int cols = 80, int rows = 24)
    {
        var sessionId = Guid.NewGuid();
        _logger.LogInformation("TerminalHub.StartSession requested. Session: {SessionId}, Dir: {Dir}, Cols: {Cols}, Rows: {Rows}", sessionId, targetDir, cols, rows);
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId.ToString());
        var session = await _sessionManager.StartSessionAsync(targetDir, requirements, cols, rows, sessionId);
        return session.Id.ToString();
    }

    /// <summary>
    /// Starts a raw POSIX shell session (e.g. /bin/bash or /bin/zsh) for terminal smoke testing.
    /// </summary>
    public async Task<string> StartShellSession(string? targetDir = null, int cols = 80, int rows = 24)
    {
        var sessionId = Guid.NewGuid();
        _logger.LogInformation("TerminalHub.StartShellSession requested. Session: {SessionId}, Dir: {Dir}, Cols: {Cols}, Rows: {Rows}", sessionId, targetDir, cols, rows);
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId.ToString());
        var session = await _sessionManager.StartShellSessionAsync(targetDir, cols, rows, sessionId);
        return session.Id.ToString();
    }

    /// <summary>
    /// Sends user keyboard input / keystrokes into the active PTY session.
    /// </summary>
    public async Task SendInput(string sessionId, string data)
    {
        if (Guid.TryParse(sessionId, out var guid))
        {
            await _sessionManager.SendInputAsync(guid, data);
        }
    }

    /// <summary>
    /// Sends terminal window resize dimensions (columns and rows) to the PTY via ioctl TIOCSWINSZ.
    /// </summary>
    public void Resize(string sessionId, int cols, int rows)
    {
        if (Guid.TryParse(sessionId, out var guid))
        {
            _sessionManager.ResizeSession(guid, cols, rows);
        }
    }

    /// <summary>
    /// Stops and terminates the specified session and its child process.
    /// </summary>
    public async Task StopSession(string sessionId)
    {
        if (Guid.TryParse(sessionId, out var guid))
        {
            await _sessionManager.StopSessionAsync(guid);
        }
    }

    /// <summary>
    /// Sends an interrupt (Ctrl+C / SIGINT) into the session PTY stream.
    /// </summary>
    public async Task InterruptSession(string sessionId)
    {
        if (Guid.TryParse(sessionId, out var guid))
        {
            await _sessionManager.InterruptSessionAsync(guid);
        }
    }

    /// <summary>
    /// Gets the current status snapshot of a session.
    /// </summary>
    public TestAgentStatusDto? GetSessionStatus(string sessionId)
    {
        if (Guid.TryParse(sessionId, out var guid))
        {
            return _sessionManager.GetSession(guid)?.ToStatusDto();
        }
        return null;
    }
}
