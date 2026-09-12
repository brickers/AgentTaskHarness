using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;

namespace AgentTaskHarness.Infrastructure.Agents;

public class NoOpAgentScheduler : IAgentScheduler
{
	public Task RequestStartAsync(Guid stepId, CancellationToken cancellationToken = default) => Task.CompletedTask;

	public Task OnAgentFinishedAsync(Guid stepId, CancellationToken cancellationToken = default) => Task.CompletedTask;

	public Task StopAgentAsync(Guid stepId, CancellationToken cancellationToken = default) => Task.CompletedTask;

	public Task ProcessQueueAsync(Guid boardId, CancellationToken cancellationToken = default) => Task.CompletedTask;

	public Task<bool> IsAgentRunningAsync(Guid stepId, CancellationToken cancellationToken = default) => Task.FromResult(false);

	public Task<AgentRunStatus?> GetStepAgentStatusAsync(Guid stepId, CancellationToken cancellationToken = default) => Task.FromResult<AgentRunStatus?>(null);

	public Task<AgentRun?> GetCurrentRunAsync(Guid stepId, CancellationToken cancellationToken = default) => Task.FromResult<AgentRun?>(null);
}
