using AgentTaskHarness.Application.Features;
using AgentTaskHarness.Application.Reviews;
using AgentTaskHarness.Application.Workflow;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Steps;

public class StepTransitionOrchestrator(
	AppDbContext dbContext,
	WorkflowTransitionRules rules,
	StepDependencyService dependencyService,
	FeatureTransitionOrchestrator featureTransitionOrchestrator,
	ReviewOutcomeService reviewOutcomeService)
{
	public async Task<Step> MoveAsync(Guid stepId, WorkflowColumn targetColumn, CancellationToken cancellationToken = default)
	{
		var step = await dbContext.Steps
			.Include(s => s.Feature)
				.ThenInclude(f => f.Board)
			.SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken)
			?? throw new KeyNotFoundException($"Step '{stepId}' was not found.");

		if (step.WorkflowColumn == WorkflowColumn.Done)
		{
			throw new InvalidOperationException("Completed steps are terminal and cannot be moved.");
		}

		var canSkipHumanReview = step.Feature?.Board is not null && step.Feature.Board.SkipStepHumanReview && !step.AlwaysRequireHumanReview;
		var effectiveTarget = targetColumn;

		// Skip human review auto-advance: when skip is active and moving from AgentReview, advance straight to Done instead of stopping at HumanReview
		if (step.WorkflowColumn == WorkflowColumn.AgentReview && targetColumn == WorkflowColumn.HumanReview && canSkipHumanReview)
		{
			effectiveTarget = WorkflowColumn.Done;
		}

		var isAllowed = rules.IsAllowedTransition(step.WorkflowColumn, effectiveTarget)
			|| (step.WorkflowColumn == WorkflowColumn.AgentReview && effectiveTarget == WorkflowColumn.Done && canSkipHumanReview);

		if (!isAllowed)
		{
			throw new InvalidOperationException($"Moving from '{step.WorkflowColumn}' to '{targetColumn}' is not allowed.");
		}

		if (step.MergeConflictPending)
		{
			throw new InvalidOperationException("Resolve the pending merge conflict before moving this step.");
		}

		// A Step can't move from Ready to Build until all its Step dependencies are complete
		if (effectiveTarget == WorkflowColumn.Build)
		{
			var dependenciesMet = await dependencyService.AreDependenciesMetAsync(stepId, cancellationToken);
			if (!dependenciesMet)
			{
				throw new InvalidOperationException("All step dependencies must be completed before moving to Build.");
			}

			// Trigger 1: first Step entering Build (Feature Ready -> Build)
			var feature = step.Feature ?? await dbContext.Features
				.SingleOrDefaultAsync(f => f.Id == step.FeatureId, cancellationToken);

			if (feature is not null && feature.WorkflowColumn == WorkflowColumn.Ready)
			{
				await featureTransitionOrchestrator.MoveAsync(feature.Id, WorkflowColumn.Build, cancellationToken);
			}
		}

		var previousColumn = step.WorkflowColumn;
		step.WorkflowColumn = effectiveTarget;

		reviewOutcomeService.HandleTransition(step, previousColumn, effectiveTarget);

		await dbContext.SaveChangesAsync(cancellationToken);

		// Trigger 2: all Steps reaching Done (Feature Build -> AgentReview)
		if (step.WorkflowColumn == WorkflowColumn.Done)
		{
			var featureSteps = await dbContext.Steps
				.Where(s => s.FeatureId == step.FeatureId)
				.ToListAsync(cancellationToken);

			if (featureSteps.Count > 0 && featureSteps.All(s => s.WorkflowColumn == WorkflowColumn.Done))
			{
				var feature = step.Feature ?? await dbContext.Features
					.SingleOrDefaultAsync(f => f.Id == step.FeatureId, cancellationToken);

				if (feature is not null && feature.WorkflowColumn == WorkflowColumn.Build)
				{
					await featureTransitionOrchestrator.MoveAsync(feature.Id, WorkflowColumn.AgentReview, cancellationToken);
				}
			}
		}

		return step;
	}

	public async Task<IReadOnlyList<WorkflowColumn>> GetAllowedMovesAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		var step = await dbContext.Steps
			.Include(s => s.Feature)
				.ThenInclude(f => f.Board)
			.SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken)
			?? throw new KeyNotFoundException($"Step '{stepId}' was not found.");

		if (step.WorkflowColumn == WorkflowColumn.Done || step.MergeConflictPending)
		{
			return [];
		}

		var canSkipHumanReview = step.Feature?.Board is not null && step.Feature.Board.SkipStepHumanReview && !step.AlwaysRequireHumanReview;
		var candidates = rules.GetAllowedTransitions(step.WorkflowColumn);
		var allowed = new List<WorkflowColumn>();

		foreach (var target in candidates)
		{
			if (target == WorkflowColumn.Build)
			{
				if (!await dependencyService.AreDependenciesMetAsync(stepId, cancellationToken))
				{
					continue;
				}
			}

			if (step.WorkflowColumn == WorkflowColumn.AgentReview && target == WorkflowColumn.HumanReview && canSkipHumanReview)
			{
				allowed.Add(WorkflowColumn.Done);
				continue;
			}

			allowed.Add(target);
		}

		return allowed;
	}
}
