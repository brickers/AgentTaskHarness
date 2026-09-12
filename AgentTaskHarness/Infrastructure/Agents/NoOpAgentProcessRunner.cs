using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Domain.Entities;

namespace AgentTaskHarness.Infrastructure.Agents;

public class NoOpAgentProcessRunner : IAgentProcessRunner
{
	public Task<AgentProcessResult> StartAsync(Step step, AgentDefinition definition,
		CancellationToken cancellationToken = default)
	{
		return Task.FromResult(new AgentProcessResult(null, null));
	}

	public Task StopAsync(int processId, CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}
}