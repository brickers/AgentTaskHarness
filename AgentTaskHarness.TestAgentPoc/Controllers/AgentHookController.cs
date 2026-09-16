using System.Text.Json;
using AgentTaskHarness.TestAgentPoc.TestAgent;
using Microsoft.AspNetCore.Mvc;

namespace AgentTaskHarness.TestAgentPoc.Controllers;

/// <summary>
/// Webhook receiver endpoint invoked by the Copilot CLI lifecycle hooks.
/// </summary>
[ApiController]
[Route("api/test-agent/hook")]
public sealed class AgentHookController : ControllerBase
{
    private readonly TestAgentSessionManager _sessionManager;
    private readonly ILogger<AgentHookController> _logger;

    public AgentHookController(TestAgentSessionManager sessionManager, ILogger<AgentHookController> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// Receives lifecycle webhook POST calls from curl commands defined in ephemeral hook configurations.
    /// </summary>
    [HttpPost("{sessionId:guid}/{eventName}")]
    public async Task<IActionResult> HandleHook(
        Guid sessionId,
        string eventName,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("AgentHookController received '{EventName}' for session {SessionId}", eventName, sessionId);

        JsonDocument? doc = null;
        if (Request.ContentLength > 0 || Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Payload for hook {EventName} could not be parsed as JSON", eventName);
            }
        }

        await _sessionManager.HandleHookEventAsync(sessionId, eventName, doc, cancellationToken);

        return Ok(new
        {
            success = true,
            sessionId = sessionId.ToString(),
            eventName = eventName,
            timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Healthcheck / diagnostic endpoint to verify hook connectivity.
    /// </summary>
    [HttpGet("ping")]
    public IActionResult Ping()
    {
        return Ok(new { status = "ok", service = "AgentHookController", time = DateTimeOffset.UtcNow });
    }
}
