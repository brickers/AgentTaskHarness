using AgentTaskHarness.Domain.Entities;

namespace AgentTaskHarness.Application.Abstractions;

public interface IGitWorktreeService
{
	Task HandleTransitionAsync(TaskItem task, Column sourceColumn, Column targetColumn, CancellationToken cancellationToken = default);
	Task DiscardWorktreeAsync(TaskItem task, CancellationToken cancellationToken = default);
	Task DiscardUncommittedChangesAsync(TaskItem task, CancellationToken cancellationToken = default);
	Task ResumeMergeAsync(TaskItem task, CancellationToken cancellationToken = default);
}

public class GitMergeConflictException : InvalidOperationException
{
	public GitMergeConflictException() : base("The task branch conflicts with main. Resolve the conflict in the repository, then resume the merge.")
	{
	}
}