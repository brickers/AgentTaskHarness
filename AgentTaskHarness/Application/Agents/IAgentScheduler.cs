using AgentTaskHarness.Domain.Entities;

namespace AgentTaskHarness.Application.Agents;

public interface IAgentScheduler
{
	Task RequestStartAsync(TaskItem task, Column column, CancellationToken cancellationToken = default);
}