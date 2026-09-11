using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Domain.Entities;

namespace AgentTaskHarness.Infrastructure.Agents;

public class NoOpAgentScheduler : IAgentScheduler
{
	public Task RequestStartAsync(TaskItem task, Column column, CancellationToken cancellationToken = default) => Task.CompletedTask;
}