using AgentTaskHarness.Application.Workflow;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Steps;

public class StepTransitionOrchestrator(
	AppDbContext dbContext,
	WorkflowTransitionRules rules,
	StepDependencyService dependencyService)
{
	public async Task<Step> MoveAsync(Guid stepId, WorkflowColumn targetColumn, CancellationToken cancellationToken = default)
	{
		var step = await dbContext.Steps
			.SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken)
			?? throw new KeyNotFoundException($"Step '{stepId}' was not found.");

		if (step.WorkflowColumn == WorkflowColumn.Done)
		{
			throw new InvalidOperationException("Completed steps are terminal and cannot be moved.");
		}

		if (!rules.IsAllowedTransition(step.WorkflowColumn, targetColumn))
		{
			throw new InvalidOperationException($"Moving from '{step.WorkflowColumn}' to '{targetColumn}' is not allowed.");
		}

		if (step.MergeConflictPending)
		{
			throw new InvalidOperationException("Resolve the pending merge conflict before moving this step.");
		}

		// A Step can't move from Ready to Build until all its Step dependencies are complete
		if (targetColumn == WorkflowColumn.Build)
		{
			var dependenciesMet = await dependencyService.AreDependenciesMetAsync(stepId, cancellationToken);
			if (!dependenciesMet)
			{
				throw new InvalidOperationException("All step dependencies must be completed before moving to Build.");
			}
		}

		step.WorkflowColumn = targetColumn;
		await dbContext.SaveChangesAsync(cancellationToken);
		return step;
	}

	public async Task<IReadOnlyList<WorkflowColumn>> GetAllowedMovesAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		var step = await dbContext.Steps
			.SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken)
			?? throw new KeyNotFoundException($"Step '{stepId}' was not found.");

		if (step.WorkflowColumn == WorkflowColumn.Done || step.MergeConflictPending)
		{
			return [];
		}

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

			allowed.Add(target);
		}

		return allowed;
	}
}
