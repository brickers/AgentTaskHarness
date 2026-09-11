using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Domain.Entities;

namespace AgentTaskHarness.Infrastructure.Git;

public class NoOpGitWorktreeService : IGitWorktreeService
{
	public Task HandleTransitionAsync(TaskItem task, Column sourceColumn, Column targetColumn, CancellationToken cancellationToken = default) => Task.CompletedTask;
}