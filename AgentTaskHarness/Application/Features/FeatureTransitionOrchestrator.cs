using AgentTaskHarness.Application.Workflow;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Features;

public class FeatureTransitionOrchestrator(
	AppDbContext dbContext,
	WorkflowTransitionRules rules,
	FeatureDependencyService dependencyService)
{
	public async Task<Feature> MoveAsync(Guid featureId, WorkflowColumn targetColumn, CancellationToken cancellationToken = default)
	{
		var feature = await dbContext.Features
			.Include(f => f.Board)
			.SingleOrDefaultAsync(f => f.Id == featureId, cancellationToken)
			?? throw new KeyNotFoundException($"Feature '{featureId}' was not found.");

		if (feature.WorkflowColumn == WorkflowColumn.Done)
		{
			throw new InvalidOperationException("Completed features are terminal and cannot be moved.");
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
		feature.WorkflowColumn = effectiveTarget;

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

		await dbContext.SaveChangesAsync(cancellationToken);
		return feature;
	}

	public async Task<IReadOnlyList<WorkflowColumn>> GetAllowedMovesAsync(Guid featureId, CancellationToken cancellationToken = default)
	{
		var feature = await dbContext.Features
			.Include(f => f.Board)
			.SingleOrDefaultAsync(f => f.Id == featureId, cancellationToken)
			?? throw new KeyNotFoundException($"Feature '{featureId}' was not found.");

		if (feature.WorkflowColumn == WorkflowColumn.Done || feature.MergeConflictPending)
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
