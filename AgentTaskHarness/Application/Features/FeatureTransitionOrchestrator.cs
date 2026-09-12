using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Application.Reviews;
using AgentTaskHarness.Application.Workflow;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Git;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Features;

public class FeatureTransitionOrchestrator(
	AppDbContext dbContext,
	WorkflowTransitionRules rules,
	FeatureDependencyService dependencyService,
	ReviewOutcomeService reviewOutcomeService,
	IGitWorktreeService? gitWorktrees = null)
{
	private readonly IGitWorktreeService gitWorktrees = gitWorktrees ?? new NoOpGitWorktreeService();

	public Task<Feature> MoveAsync(Guid featureId, WorkflowColumn targetColumn, CancellationToken cancellationToken = default) =>
		MoveAsync(featureId, targetColumn, discardWorktreeOnBacklogReturn: false, isMcpMove: false, cancellationToken);

	public Task<Feature> MoveAndDiscardWorktreeAsync(Guid featureId, WorkflowColumn targetColumn, CancellationToken cancellationToken = default) =>
		MoveAsync(featureId, targetColumn, discardWorktreeOnBacklogReturn: true, isMcpMove: false, cancellationToken);

	public Task<Feature> MoveAsync(Guid featureId, WorkflowColumn targetColumn, bool discardWorktreeOnBacklogReturn, CancellationToken cancellationToken = default) =>
		MoveAsync(featureId, targetColumn, discardWorktreeOnBacklogReturn, isMcpMove: false, cancellationToken);

	public Task<Feature> MoveMcpAsync(Guid featureId, WorkflowColumn targetColumn, CancellationToken cancellationToken = default) =>
		MoveAsync(featureId, targetColumn, discardWorktreeOnBacklogReturn: false, isMcpMove: true, cancellationToken);

	public async Task<Feature> MoveAsync(Guid featureId, WorkflowColumn targetColumn, bool discardWorktreeOnBacklogReturn, bool isMcpMove, CancellationToken cancellationToken = default)
	{
		var feature = await dbContext.Features
			.Include(f => f.Board)
			.SingleOrDefaultAsync(f => f.Id == featureId, cancellationToken)
			?? throw new KeyNotFoundException($"Feature '{featureId}' was not found.");

		if (feature.WorkflowColumn == WorkflowColumn.Done)
		{
			throw new InvalidOperationException("Completed features are terminal and cannot be moved.");
		}

		// Hard-block UI moves if an agent is currently running. MCP moves allow the transition and apply the soft blocked flag.
		var hasActiveAgent = await dbContext.AgentRuns.AnyAsync(r =>
			r.CardType == CardType.Feature && r.CardId == featureId &&
			(r.Status == AgentRunStatus.Working || r.Status == AgentRunStatus.WaitingForInput),
			cancellationToken);

		if (!isMcpMove && hasActiveAgent)
		{
			throw new InvalidOperationException("Cannot move a feature while an agent is running.");
		}

		var canSkipHumanReview = feature.Board is not null && feature.Board.SkipFeatureHumanReview && !feature.AlwaysRequireHumanReview;
		var effectiveTarget = targetColumn;

		// Skip human review auto-advance: when skip is active and moving from AgentReview, advance straight to Done instead of stopping at HumanReview
		if (feature.WorkflowColumn == WorkflowColumn.AgentReview && targetColumn == WorkflowColumn.HumanReview && canSkipHumanReview)
		{
			effectiveTarget = WorkflowColumn.Done;
		}

		var isAllowed = rules.IsAllowedTransition(feature.WorkflowColumn, effectiveTarget)
			|| (feature.WorkflowColumn == WorkflowColumn.AgentReview && effectiveTarget == WorkflowColumn.Done && canSkipHumanReview);

		if (!isAllowed)
		{
			throw new InvalidOperationException($"Moving from '{feature.WorkflowColumn}' to '{targetColumn}' is not allowed.");
		}

		if (feature.MergeConflictPending)
		{
			throw new InvalidOperationException("Resolve the pending merge conflict before moving this feature.");
		}

		if (feature.WorkflowColumn == WorkflowColumn.Backlog && effectiveTarget == WorkflowColumn.Ready)
		{
			var dependenciesMet = await dependencyService.AreDependenciesMetAsync(featureId, cancellationToken);
			if (!dependenciesMet)
			{
				throw new InvalidOperationException("All feature dependencies must be completed before moving to Ready.");
			}
		}

		var previousColumn = feature.WorkflowColumn;

		try
		{
			if (effectiveTarget == WorkflowColumn.Backlog && discardWorktreeOnBacklogReturn)
			{
				await gitWorktrees.DiscardFeatureWorktreeAsync(feature, cancellationToken);
			}
			else
			{
				await gitWorktrees.HandleFeatureTransitionAsync(feature, previousColumn, effectiveTarget, cancellationToken);
			}
		}
		catch (GitMergeConflictException)
		{
			feature.MergeConflictPending = true;
			await dbContext.SaveChangesAsync(cancellationToken);
			throw;
		}

		feature.WorkflowColumn = effectiveTarget;

		reviewOutcomeService.HandleTransition(feature, previousColumn, effectiveTarget);

		// Once a Feature successfully moves to Ready, all of its Steps are moved to Ready together as one batch
		if (previousColumn == WorkflowColumn.Backlog && effectiveTarget == WorkflowColumn.Ready)
		{
			var backlogSteps = await dbContext.Steps
				.Where(s => s.FeatureId == featureId && s.WorkflowColumn == WorkflowColumn.Backlog)
				.ToListAsync(cancellationToken);

			foreach (var step in backlogSteps)
			{
				step.WorkflowColumn = WorkflowColumn.Ready;
			}
		}

		if (isMcpMove && hasActiveAgent)
		{
			dbContext.AgentRuns.Add(new AgentRun
			{
				CardType = CardType.Feature,
				CardId = featureId,
				Status = AgentRunStatus.Blocked,
				StartedAt = DateTimeOffset.UtcNow
			});
		}

		await dbContext.SaveChangesAsync(cancellationToken);
		return feature;
	}

	public async Task<Feature> ResumeMergeAsync(Guid featureId, CancellationToken cancellationToken = default)
	{
		var feature = await dbContext.Features
			.Include(f => f.Board)
			.SingleOrDefaultAsync(f => f.Id == featureId, cancellationToken)
			?? throw new KeyNotFoundException($"Feature '{featureId}' was not found.");

		if (!feature.MergeConflictPending)
		{
			throw new InvalidOperationException("This feature does not have a merge conflict pending.");
		}

		await gitWorktrees.ResumeFeatureMergeAsync(feature, cancellationToken);
		feature.WorkflowColumn = WorkflowColumn.Done;
		feature.MergeConflictPending = false;
		await dbContext.SaveChangesAsync(cancellationToken);
		return feature;
	}

	public async Task DiscardUncommittedChangesAsync(Guid featureId, CancellationToken cancellationToken = default)
	{
		var feature = await dbContext.Features
			.Include(f => f.Board)
			.SingleOrDefaultAsync(f => f.Id == featureId, cancellationToken)
			?? throw new KeyNotFoundException($"Feature '{featureId}' was not found.");

		await gitWorktrees.DiscardFeatureUncommittedChangesAsync(feature, cancellationToken);
	}

	public async Task DiscardWorktreeAsync(Guid featureId, CancellationToken cancellationToken = default)
	{
		var feature = await dbContext.Features
			.Include(f => f.Board)
			.SingleOrDefaultAsync(f => f.Id == featureId, cancellationToken)
			?? throw new KeyNotFoundException($"Feature '{featureId}' was not found.");

		await gitWorktrees.DiscardFeatureWorktreeAsync(feature, cancellationToken);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public async Task<IReadOnlyList<WorkflowColumn>> GetAllowedMovesAsync(Guid featureId, CancellationToken cancellationToken = default)
	{
		var feature = await dbContext.Features
			.Include(f => f.Board)
			.SingleOrDefaultAsync(f => f.Id == featureId, cancellationToken)
			?? throw new KeyNotFoundException($"Feature '{featureId}' was not found.");

		// UI hard-blocks moving a card while an agent is currently running
		var hasActiveAgent = await dbContext.AgentRuns.AnyAsync(r =>
			r.CardType == CardType.Feature && r.CardId == featureId &&
			(r.Status == AgentRunStatus.Working || r.Status == AgentRunStatus.WaitingForInput),
			cancellationToken);

		if (feature.WorkflowColumn == WorkflowColumn.Done || feature.MergeConflictPending || hasActiveAgent)
		{
			return [];
		}

		var canSkipHumanReview = feature.Board is not null && feature.Board.SkipFeatureHumanReview && !feature.AlwaysRequireHumanReview;
		var candidates = rules.GetAllowedTransitions(feature.WorkflowColumn);
		var allowed = new List<WorkflowColumn>();

		foreach (var target in candidates)
		{
			if (feature.WorkflowColumn == WorkflowColumn.Backlog && target == WorkflowColumn.Ready)
			{
				if (!await dependencyService.AreDependenciesMetAsync(featureId, cancellationToken))
				{
					continue;
				}
			}

			if (feature.WorkflowColumn == WorkflowColumn.AgentReview && target == WorkflowColumn.HumanReview && canSkipHumanReview)
			{
				allowed.Add(WorkflowColumn.Done);
				continue;
			}

			allowed.Add(target);
		}

		return allowed;
	}
}
