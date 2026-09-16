namespace AgentTaskHarness.TestAgentPoc.TestAgent;

/// <summary>
/// Inactivity watchdog monitor that detects hung or stalled Copilot sessions
/// and transitions their state to Stalled after a configurable duration.
/// </summary>
public sealed class AgentWatchdogTimer : IHostedService, IDisposable
{
    private readonly TestAgentSessionManager _sessionManager;
    private readonly ILogger<AgentWatchdogTimer> _logger;
    private readonly PeriodicTimer _timer;
    private CancellationTokenSource? _cts;
    private Task? _executingTask;

    /// <summary>
    /// Watchdog inactivity threshold before marking an active session as Stalled.
    /// Default: 180 seconds (3 minutes) per design document §4.
    /// </summary>
    public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromSeconds(180);

    public AgentWatchdogTimer(
        TestAgentSessionManager sessionManager,
        IConfiguration configuration,
        ILogger<AgentWatchdogTimer> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;

        if (int.TryParse(configuration["TestAgent:WatchdogTimeoutSeconds"], out int seconds) && seconds > 0)
        {
            InactivityTimeout = TimeSpan.FromSeconds(seconds);
        }

        // Ticks every 3 seconds to check inactivity
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting AgentWatchdogTimer with timeout of {TimeoutSeconds}s", InactivityTimeout.TotalSeconds);
        _cts = new CancellationTokenSource();
        _executingTask = RunWatchdogLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping AgentWatchdogTimer");
        if (_cts != null)
        {
            await _cts.CancelAsync();
        }

        if (_executingTask != null)
        {
            await Task.WhenAny(_executingTask, Task.Delay(Timeout.Infinite, cancellationToken));
        }
    }

    public async Task CheckSessionsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var activeSessions = _sessionManager.GetAllSessions();

        foreach (var session in activeSessions)
        {
            // Only monitor sessions in Thinking or ToolExecuting states
            if (session.State is TestAgentState.Thinking or TestAgentState.ToolExecuting)
            {
                var elapsed = now - session.LastActivityTimestamp;
                if (elapsed > InactivityTimeout)
                {
                    _logger.LogWarning(
                        "Session {SessionId} has been inactive for {ElapsedSeconds:F1}s (threshold: {Threshold}s). Marking as Stalled.",
                        session.Id,
                        elapsed.TotalSeconds,
                        InactivityTimeout.TotalSeconds);

                    if (session.TransitionState(TestAgentState.Stalled))
                    {
                        await _sessionManager.NotifyStateChangedAsync(session);
                    }
                }
            }
        }
    }

    public async Task InterruptAsync(Guid sessionId)
    {
        _logger.LogInformation("Watchdog triggering interrupt on session {SessionId}", sessionId);
        await _sessionManager.InterruptSessionAsync(sessionId);
    }

    private async Task RunWatchdogLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && await _timer.WaitForNextTickAsync(cancellationToken))
            {
                await CheckSessionsAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in watchdog loop");
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _cts?.Dispose();
    }
}
