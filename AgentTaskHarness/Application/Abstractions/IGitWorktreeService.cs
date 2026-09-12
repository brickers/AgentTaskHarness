using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;

namespace AgentTaskHarness.Application.Abstractions;

public interface IGitWorktreeService
{
	Task HandleFeatureTransitionAsync(Feature feature, WorkflowColumn sourceColumn, WorkflowColumn targetColumn,
		CancellationToken cancellationToken = default);

	Task HandleStepTransitionAsync(Step step, WorkflowColumn sourceColumn, WorkflowColumn targetColumn,
		CancellationToken cancellationToken = default);

	Task DiscardFeatureWorktreeAsync(Feature feature, CancellationToken cancellationToken = default);
	Task DiscardStepWorktreeAsync(Step step, CancellationToken cancellationToken = default);
	Task DiscardFeatureUncommittedChangesAsync(Feature feature, CancellationToken cancellationToken = default);
	Task DiscardStepUncommittedChangesAsync(Step step, CancellationToken cancellationToken = default);
	Task ResumeFeatureMergeAsync(Feature feature, CancellationToken cancellationToken = default);
	Task ResumeStepMergeAsync(Step step, CancellationToken cancellationToken = default);
}

public class GitMergeConflictException : InvalidOperationException
{
	public GitMergeConflictException() : base(
		"Merge conflict detected. Resolve the conflict in the repository, then resume.")
	{
	}
}