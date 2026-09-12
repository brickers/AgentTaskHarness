using AgentTaskHarness.Domain.Entities;

namespace AgentTaskHarness.Application.Abstractions;

public record AgentProcessResult(int? ProcessId, string? SessionLink);

public interface IAgentProcessRunner
{
	Task<AgentProcessResult> StartAsync(Step step, AgentDefinition definition,
		CancellationToken cancellationToken = default);

	Task StopAsync(int processId, CancellationToken cancellationToken = default);
}