using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;

namespace AgentTaskHarness.Infrastructure.Git;

public class NoOpGitWorktreeService : IGitWorktreeService
{
	public Task HandleFeatureTransitionAsync(Feature feature, WorkflowColumn sourceColumn, WorkflowColumn targetColumn, CancellationToken cancellationToken = default) => Task.CompletedTask;
	public Task HandleStepTransitionAsync(Step step, WorkflowColumn sourceColumn, WorkflowColumn targetColumn, CancellationToken cancellationToken = default) => Task.CompletedTask;
	public Task DiscardFeatureWorktreeAsync(Feature feature, CancellationToken cancellationToken = default) => Task.CompletedTask;
	public Task DiscardStepWorktreeAsync(Step step, CancellationToken cancellationToken = default) => Task.CompletedTask;
	public Task DiscardFeatureUncommittedChangesAsync(Feature feature, CancellationToken cancellationToken = default) => Task.CompletedTask;
	public Task DiscardStepUncommittedChangesAsync(Step step, CancellationToken cancellationToken = default) => Task.CompletedTask;
	public Task ResumeFeatureMergeAsync(Feature feature, CancellationToken cancellationToken = default) => Task.CompletedTask;
	public Task ResumeStepMergeAsync(Step step, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
