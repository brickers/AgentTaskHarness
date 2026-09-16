using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AgentTaskHarness.TestAgentPoc.Hubs;
using AgentTaskHarness.TestAgentPoc.Pty;
using Microsoft.AspNetCore.SignalR;

namespace AgentTaskHarness.TestAgentPoc.TestAgent;

/// <summary>
/// Central manager coordinating test agent session lifecycles, POSIX PTY process execution,
/// ephemeral hook configuration generation, hook event dispatching, and UI notifications.
/// </summary>
public sealed class TestAgentSessionManager : IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<Guid, TestAgentSession> _sessions = new();
    private readonly IPtyService _ptyService;
    private readonly IHubContext<TerminalHub> _hubContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TestAgentSessionManager> _logger;
    private readonly List<string> _tempDirsToClean = new();
    private readonly object _cleanupLock = new();

    public TestAgentSessionManager(
        IPtyService ptyService,
        IHubContext<TerminalHub> hubContext,
        IConfiguration configuration,
        ILogger<TestAgentSessionManager> logger)
    {
        _ptyService = ptyService;
        _hubContext = hubContext;
        _configuration = configuration;
        _logger = logger;
    }

    public TestAgentSession? GetSession(Guid sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public IReadOnlyCollection<TestAgentSession> GetAllSessions()
    {
        return _sessions.Values.ToList();
    }

    internal void RegisterTestSession(TestAgentSession session)
    {
        _sessions[session.Id] = session;
    }

    /// <summary>
    /// Starts a Copilot agent test session configured with the given working directory and requirements.
    /// </summary>
    public async Task<TestAgentSession> StartSessionAsync(
        string targetDir,
        string requirements,
        int cols = 80,
        int rows = 24,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDir);

        var id = sessionId ?? Guid.NewGuid();
        var session = new TestAgentSession(
            id,
            targetDir,
            requirements,
            isShell: false,
            initialState: TestAgentState.Initializing);

        _sessions[id] = session;
        _logger.LogInformation("Creating test agent session {SessionId} for {TargetDir}", id, targetDir);

        int port = ResolvePort();
        var (hooksDir, targetHookFile) = SetupHookConfigurations(id, targetDir, port);
        session.TempHooksDir = hooksDir;
        session.TargetHookFile = targetHookFile;

        string prompt = string.IsNullOrWhiteSpace(requirements)
            ? DefaultTestAgentDefinition.Prompt
            : $"{DefaultTestAgentDefinition.Prompt}\n\nTask Requirements:\n{requirements.Trim()}";

        var ptySession = _ptyService.StartCopilotSession(
            targetDir: targetDir,
            prompt: prompt,
            cols: cols,
            rows: rows,
            environmentVariables: new Dictionary<string, string>
            {
                ["TEST_AGENT_SESSION_ID"] = id.ToString(),
                ["TEST_AGENT_PORT"] = port.ToString()
            });

        session.PtySession = ptySession;
        session.TransitionState(TestAgentState.Initializing);

        AttachOutputPump(session, targetHookFile);
        await NotifyStateChangedAsync(session);

        return session;
    }

    /// <summary>
    /// Starts a raw shell session (e.g. bash or zsh) for terminal connectivity and interop testing.
    /// </summary>
    public async Task<TestAgentSession> StartShellSessionAsync(
        string? targetDir = null,
        int cols = 80,
        int rows = 24,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var id = sessionId ?? Guid.NewGuid();
        string workingDir = !string.IsNullOrWhiteSpace(targetDir) && Directory.Exists(targetDir)
            ? targetDir
            : Environment.CurrentDirectory;

        var session = new TestAgentSession(
            id,
            workingDir,
            requirements: "Interactive Shell Smoke Test",
            isShell: true,
            initialState: TestAgentState.Thinking);

        _sessions[id] = session;
        _logger.LogInformation("Creating shell test session {SessionId} in {WorkingDir}", id, workingDir);

        string shell = File.Exists("/bin/zsh") ? "/bin/zsh" : (File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh");
        var startInfo = new PtyStartInfo(
            Command: shell,
            Arguments: new[] { "-l" },
            WorkingDirectory: workingDir,
            InitialCols: cols,
            InitialRows: rows);

        var ptySession = _ptyService.StartSession(startInfo);
        session.PtySession = ptySession;

        AttachOutputPump(session, targetHookFile: null);
        await NotifyStateChangedAsync(session);

        return session;
    }

    /// <summary>
    /// Sends user keyboard data to the session's PTY.
    /// </summary>
    public async Task SendInputAsync(Guid sessionId, string input, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.PtySession != null)
        {
            session.TouchActivity();

            // If session was flagged as Stalled and user inputs text, return to Thinking
            if (session.State == TestAgentState.Stalled)
            {
                session.TransitionState(TestAgentState.Thinking);
                await NotifyStateChangedAsync(session);
            }

            await session.PtySession.WriteAsync(input, cancellationToken);
        }
    }

    /// <summary>
    /// Resizes the PTY terminal window dimensions.
    /// </summary>
    public void ResizeSession(Guid sessionId, int cols, int rows)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.PtySession != null)
        {
            session.PtySession.Resize(cols, rows);
        }
    }

    /// <summary>
    /// Interrupts the running process by sending SIGINT / Ctrl+C (0x03).
    /// </summary>
    public async Task InterruptSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.PtySession != null)
        {
            _logger.LogInformation("Interrupting session {SessionId} with Ctrl+C", sessionId);
            session.TouchActivity();
            await session.PtySession.WriteAsync(new byte[] { 0x03 }, cancellationToken);

            if (session.State is TestAgentState.Stalled or TestAgentState.ToolExecuting)
            {
                session.TransitionState(TestAgentState.Thinking);
                await NotifyStateChangedAsync(session);
            }
        }
    }

    /// <summary>
    /// Stops and terminates the specified session.
    /// </summary>
    public async Task StopSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            _logger.LogInformation("Stopping session {SessionId}", sessionId);
            if (session.PtySession != null && session.PtySession.IsRunning)
            {
                session.PtySession.Kill(NativePosixPty.SIGTERM);
            }

            CleanupSessionFiles(session);
            session.TransitionState(TestAgentState.Finished);
            await NotifyStateChangedAsync(session);
        }
    }

    /// <summary>
    /// Handles lifecycle webhook POSTs received from the Copilot CLI.
    /// </summary>
    public async Task HandleHookEventAsync(
        Guid sessionId,
        string eventName,
        JsonDocument? payload,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            _logger.LogWarning("Hook event '{EventName}' received for unknown session {SessionId}", eventName, sessionId);
            return;
        }

        _logger.LogInformation("Hook event '{EventName}' received for session {SessionId}", eventName, sessionId);
        session.TouchActivity();

        bool stateChanged = false;

        switch (eventName.Trim().ToLowerInvariant())
        {
            case "sessionstart":
            case "userpromptsubmitted":
                stateChanged = session.TransitionState(TestAgentState.Thinking);
                break;

            case "pretooluse":
                string? toolName = ExtractToolName(payload);
                string? toolArgs = ExtractToolArgs(payload);
                bool requiresApproval = CheckRequiresApproval(payload);

                if (string.Equals(toolName, "ask_user", StringComparison.OrdinalIgnoreCase))
                {
                    stateChanged = session.TransitionState(TestAgentState.WaitingForUser, toolName, toolArgs);
                }
                else if (requiresApproval)
                {
                    stateChanged = session.TransitionState(TestAgentState.WaitingForApproval, toolName, toolArgs);
                }
                else
                {
                    stateChanged = session.TransitionState(TestAgentState.ToolExecuting, toolName, toolArgs);
                }
                break;

            case "posttooluse":
                stateChanged = session.TransitionState(TestAgentState.Thinking);
                break;

            case "agentstop":
                stateChanged = session.TransitionState(TestAgentState.WaitingForUser);
                break;

            case "erroroccurred":
                string? errMsg = ExtractErrorMessage(payload);
                stateChanged = session.TransitionState(TestAgentState.Failed, failureReason: errMsg ?? "Copilot CLI reported error");
                break;

            case "sessionend":
                stateChanged = session.TransitionState(TestAgentState.Finished);
                break;

            default:
                _logger.LogDebug("Unhandled hook event: {EventName}", eventName);
                break;
        }

        if (stateChanged)
        {
            await NotifyStateChangedAsync(session);
        }
    }

    public async Task NotifyStateChangedAsync(TestAgentSession session)
    {
        try
        {
            await _hubContext.Clients.Group(session.Id.ToString())
                .SendAsync("SessionStateChanged", session.Id.ToString(), session.ToStatusDto());
            await _hubContext.Clients.All
                .SendAsync("SessionStateChanged", session.Id.ToString(), session.ToStatusDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast state change for session {SessionId}", session.Id);
        }
    }

    private void AttachOutputPump(TestAgentSession session, string? targetHookFile)
    {
        session.PtySession?.StartOutputPump(
            onData: async (chunk, ct) =>
            {
                session.TouchActivity();
                string text = Encoding.UTF8.GetString(chunk);

                // Broadcast raw output chunk to listeners in the session group and all connected clients
                try
                {
                    await _hubContext.Clients.Group(session.Id.ToString())
                        .SendAsync("ReceiveOutput", session.Id.ToString(), text, ct);
                    await _hubContext.Clients.All
                        .SendAsync("ReceiveOutput", session.Id.ToString(), text, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "Failed to broadcast output chunk for session {SessionId}", session.Id);
                }
            },
            onExit: async exitCode =>
            {
                _logger.LogInformation("Session {SessionId} process exited with code {ExitCode}", session.Id, exitCode);

                if (targetHookFile != null && File.Exists(targetHookFile))
                {
                    try { File.Delete(targetHookFile); } catch { /* Ignore */ }
                }

                CleanupSessionFiles(session);

                if (session.State is not (TestAgentState.Finished or TestAgentState.Failed))
                {
                    var finalState = exitCode == 0 ? TestAgentState.Finished : TestAgentState.Failed;
                    string? reason = exitCode != 0 ? $"Process exited with code {exitCode}" : null;
                    session.TransitionState(finalState, exitCode: exitCode, failureReason: reason);
                }

                await NotifyStateChangedAsync(session);

                try
                {
                    await _hubContext.Clients.Group(session.Id.ToString())
                        .SendAsync("SessionFinished", session.Id.ToString(), exitCode);
                    await _hubContext.Clients.All
                        .SendAsync("SessionFinished", session.Id.ToString(), exitCode);
                }
                catch { /* Ignore */ }
            });
    }

    private (string tempHooksDir, string? targetHookFile) SetupHookConfigurations(Guid sessionId, string targetDir, int port)
    {
        // 1. Create dedicated ephemeral hooks directory in .agent-temp
        string tempRoot = Path.Combine(Directory.GetCurrentDirectory(), ".agent-temp", $"session-{sessionId}");
        string hooksSubdir = Path.Combine(tempRoot, ".github", "hooks");
        Directory.CreateDirectory(hooksSubdir);

        string hookJson = GenerateHookConfigJson(sessionId, port);
        string hookFilePath = Path.Combine(hooksSubdir, "hooks.json");
        File.WriteAllText(hookFilePath, hookJson);

        // Also create .agent-temp/test-hooks-{sessionId}.json matching plan
        string altTempHookPath = Path.Combine(Directory.GetCurrentDirectory(), ".agent-temp", $"test-hooks-{sessionId}.json");
        File.WriteAllText(altTempHookPath, hookJson);

        lock (_cleanupLock)
        {
            _tempDirsToClean.Add(tempRoot);
            _tempDirsToClean.Add(altTempHookPath);
        }

        // 2. Also write to targetDir/.github/hooks if targetDir exists to guarantee hook discovery
        string? targetHookFile = null;
        try
        {
            if (Directory.Exists(targetDir))
            {
                string targetHooksDir = Path.Combine(targetDir, ".github", "hooks");
                Directory.CreateDirectory(targetHooksDir);
                targetHookFile = Path.Combine(targetHooksDir, $"test-agent-{sessionId}.json");
                File.WriteAllText(targetHookFile, hookJson);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write hook file into target directory {TargetDir}", targetDir);
        }

        return (tempRoot, targetHookFile);
    }

    private static string GenerateHookConfigJson(Guid sessionId, int port)
    {
        return $$"""
        {
          "version": 1,
          "hooks": {
            "sessionStart": [
              { "type": "command", "bash": "curl -s -X POST http://127.0.0.1:{{port}}/api/test-agent/hook/{{sessionId}}/sessionStart -H 'Content-Type: application/json' -d @-" }
            ],
            "userPromptSubmitted": [
              { "type": "command", "bash": "curl -s -X POST http://127.0.0.1:{{port}}/api/test-agent/hook/{{sessionId}}/userPromptSubmitted -H 'Content-Type: application/json' -d @-" }
            ],
            "preToolUse": [
              { "type": "command", "bash": "curl -s -X POST http://127.0.0.1:{{port}}/api/test-agent/hook/{{sessionId}}/preToolUse -H 'Content-Type: application/json' -d @-" }
            ],
            "postToolUse": [
              { "type": "command", "bash": "curl -s -X POST http://127.0.0.1:{{port}}/api/test-agent/hook/{{sessionId}}/postToolUse -H 'Content-Type: application/json' -d @-" }
            ],
            "agentStop": [
              { "type": "command", "bash": "curl -s -X POST http://127.0.0.1:{{port}}/api/test-agent/hook/{{sessionId}}/agentStop -H 'Content-Type: application/json' -d @-" }
            ],
            "errorOccurred": [
              { "type": "command", "bash": "curl -s -X POST http://127.0.0.1:{{port}}/api/test-agent/hook/{{sessionId}}/errorOccurred -H 'Content-Type: application/json' -d @-" }
            ],
            "sessionEnd": [
              { "type": "command", "bash": "curl -s -X POST http://127.0.0.1:{{port}}/api/test-agent/hook/{{sessionId}}/sessionEnd -H 'Content-Type: application/json' -d @-" }
            ]
          }
        }
        """;
    }

    private void CleanupSessionFiles(TestAgentSession session)
    {
        if (session.TempHooksDir != null && Directory.Exists(session.TempHooksDir))
        {
            try
            {
                Directory.Delete(session.TempHooksDir, recursive: true);
            }
            catch { /* Ignore cleanup errors */ }
        }

        if (session.TargetHookFile != null && File.Exists(session.TargetHookFile))
        {
            try
            {
                File.Delete(session.TargetHookFile);
            }
            catch { /* Ignore */ }
        }

        string altTempHookPath = Path.Combine(Directory.GetCurrentDirectory(), ".agent-temp", $"test-hooks-{session.Id}.json");
        if (File.Exists(altTempHookPath))
        {
            try { File.Delete(altTempHookPath); } catch { /* Ignore */ }
        }
    }

    private int ResolvePort()
    {
        if (int.TryParse(_configuration["TestAgent:HookPort"], out int p) && p > 0)
        {
            return p;
        }

        string? urls = _configuration["urls"] ?? _configuration["ASPNETCORE_URLS"];
        if (!string.IsNullOrEmpty(urls))
        {
            foreach (var url in urls.Split(';'))
            {
                if (Uri.TryCreate(url.Replace("+", "localhost").Replace("*", "localhost"), UriKind.Absolute, out var uri))
                {
                    if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                    {
                        return uri.Port;
                    }
                }
            }
        }

        return 5299;
    }

    private static string? ExtractToolName(JsonDocument? doc)
    {
        if (doc == null) return null;
        var root = doc.RootElement;

        if (root.TryGetProperty("toolName", out var tn)) return tn.GetString();
        if (root.TryGetProperty("tool", out var t)) return t.GetString();
        if (root.TryGetProperty("name", out var n)) return n.GetString();
        if (root.TryGetProperty("toolUse", out var tu) && tu.TryGetProperty("name", out var tun)) return tun.GetString();

        return null;
    }

    private static string? ExtractToolArgs(JsonDocument? doc)
    {
        if (doc == null) return null;
        var root = doc.RootElement;

        if (root.TryGetProperty("toolArgs", out var ta)) return ta.ToString();
        if (root.TryGetProperty("arguments", out var a)) return a.ToString();
        if (root.TryGetProperty("toolUse", out var tu) && tu.TryGetProperty("arguments", out var tua)) return tua.ToString();

        return null;
    }

    private static bool CheckRequiresApproval(JsonDocument? doc)
    {
        if (doc == null) return false;
        var root = doc.RootElement;

        if (root.TryGetProperty("requiresApproval", out var ra) && ra.ValueKind is JsonValueKind.True) return true;
        if (root.TryGetProperty("needsApproval", out var na) && na.ValueKind is JsonValueKind.True) return true;
        if (root.TryGetProperty("askApproval", out var aa) && aa.ValueKind is JsonValueKind.True) return true;

        return false;
    }

    private static string? ExtractErrorMessage(JsonDocument? doc)
    {
        if (doc == null) return null;
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var e)) return e.GetString();
        if (root.TryGetProperty("message", out var m)) return m.GetString();

        return null;
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            try { session.PtySession?.Dispose(); } catch { }
            CleanupSessionFiles(session);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            if (session.PtySession != null)
            {
                try { await session.PtySession.DisposeAsync(); } catch { }
            }
            CleanupSessionFiles(session);
        }
    }
}
