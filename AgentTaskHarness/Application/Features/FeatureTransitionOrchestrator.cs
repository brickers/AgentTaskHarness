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
			.SingleOrDefaultAsync(f => f.Id == featureId, cancellationToken)
			?? throw new KeyNotFoundException($"Feature '{featureId}' was not found.");

		if (feature.WorkflowColumn == WorkflowColumn.Done)
		{
			throw new InvalidOperationException("Completed features are terminal and cannot be moved.");
		}

		if (!rules.IsAllowedTransition(feature.WorkflowColumn, targetColumn))
		{
			throw new InvalidOperationException($"Moving from '{feature.WorkflowColumn}' to '{targetColumn}' is not allowed.");
		}

		if (feature.MergeConflictPending)
		{
			throw new InvalidOperationException("Resolve the pending merge conflict before moving this feature.");
		}

		if (feature.WorkflowColumn == WorkflowColumn.Backlog && targetColumn == WorkflowColumn.Ready)
		{
			var dependenciesMet = await dependencyService.AreDependenciesMetAsync(featureId, cancellationToken);
			if (!dependenciesMet)
			{
				throw new InvalidOperationException("All feature dependencies must be completed before moving to Ready.");
			}
		}

		var previousColumn = feature.WorkflowColumn;
		feature.WorkflowColumn = targetColumn;

		// Once a Feature successfully moves to Ready, all of its Steps are moved to Ready together as one batch
		if (previousColumn == WorkflowColumn.Backlog && targetColumn == WorkflowColumn.Ready)
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
			.SingleOrDefaultAsync(f => f.Id == featureId, cancellationToken)
			?? throw new KeyNotFoundException($"Feature '{featureId}' was not found.");

		if (feature.WorkflowColumn == WorkflowColumn.Done || feature.MergeConflictPending)
		{
			return [];
		}

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

			allowed.Add(target);
		}

		return allowed;
	}
}
