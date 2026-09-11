using AgentTaskHarness.Domain.Entities;

namespace AgentTaskHarness.Application.Abstractions;

public interface IGitWorktreeService
{
	Task HandleTransitionAsync(TaskItem task, Column sourceColumn, Column targetColumn, CancellationToken cancellationToken = default);
}