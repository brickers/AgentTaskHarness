using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Steps;

public class StepDependencyService(AppDbContext dbContext)
{
	public async Task AddAsync(Guid stepId, Guid dependsOnStepId, CancellationToken cancellationToken = default)
	{
		if (stepId == dependsOnStepId)
		{
			throw new InvalidOperationException("A step cannot depend on itself.");
		}

		var step = await FindStepAsync(stepId, cancellationToken);
		EnsureDependenciesAreEditable(step);

		var dependsOn = await FindStepAsync(dependsOnStepId, cancellationToken);
		if (step.FeatureId != dependsOn.FeatureId)
		{
			throw new InvalidOperationException("Step dependencies must remain within the same feature.");
		}

		if (await dbContext.StepDependencies.AnyAsync(d => d.StepId == stepId && d.DependsOnStepId == dependsOnStepId, cancellationToken))
		{
			return;
		}

		if (await WouldCreateCycleAsync(stepId, dependsOnStepId, cancellationToken))
		{
			throw new InvalidOperationException("Circular dependencies are not allowed.");
		}

		dbContext.StepDependencies.Add(new StepDependency
		{
			StepId = stepId,
			DependsOnStepId = dependsOnStepId
		});
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public async Task RemoveAsync(Guid stepId, Guid dependsOnStepId, CancellationToken cancellationToken = default)
	{
		var step = await FindStepAsync(stepId, cancellationToken);
		EnsureDependenciesAreEditable(step);

		var dependency = await dbContext.StepDependencies.SingleOrDefaultAsync(
			d => d.StepId == stepId && d.DependsOnStepId == dependsOnStepId,
			cancellationToken);

		if (dependency is null)
		{
			return;
		}

		dbContext.StepDependencies.Remove(dependency);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public Task<List<StepDependency>> GetForStepAsync(Guid stepId, CancellationToken cancellationToken = default) =>
		dbContext.StepDependencies
			.AsNoTracking()
			.Include(d => d.DependsOnStep)
			.Where(d => d.StepId == stepId)
			.ToListAsync(cancellationToken);

	public async Task<bool> AreDependenciesMetAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		var hasUnfinished = await dbContext.StepDependencies
			.Where(d => d.StepId == stepId)
			.AnyAsync(d => d.DependsOnStep.WorkflowColumn != WorkflowColumn.Done, cancellationToken);

		return !hasUnfinished;
	}

	public Task<List<Step>> GetUnmetDependenciesAsync(Guid stepId, CancellationToken cancellationToken = default) =>
		dbContext.StepDependencies
			.AsNoTracking()
			.Where(d => d.StepId == stepId && d.DependsOnStep.WorkflowColumn != WorkflowColumn.Done)
			.Select(d => d.DependsOnStep)
			.ToListAsync(cancellationToken);

	private async Task<Step> FindStepAsync(Guid stepId, CancellationToken cancellationToken) =>
		await dbContext.Steps.SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken)
		?? throw new KeyNotFoundException($"Step '{stepId}' was not found.");

	private static void EnsureDependenciesAreEditable(Step step)
	{
		if (step.WorkflowColumn == WorkflowColumn.Backlog)
		{
			return;
		}

		if (step.WorkflowColumn == WorkflowColumn.Ready && step.BranchName is null)
		{
			return;
		}

		throw new InvalidOperationException("Step dependencies can only be changed before starting or after returning to the backlog.");
	}

	private async Task<bool> WouldCreateCycleAsync(Guid stepId, Guid dependsOnStepId, CancellationToken cancellationToken)
	{
		var visited = new HashSet<Guid>();
		var queue = new Queue<Guid>();
		queue.Enqueue(dependsOnStepId);

		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			if (current == stepId)
			{
				return true;
			}

			if (visited.Add(current))
			{
				var nextDependencies = await dbContext.StepDependencies
					.Where(d => d.StepId == current)
					.Select(d => d.DependsOnStepId)
					.ToListAsync(cancellationToken);

				foreach (var next in nextDependencies)
				{
					if (!visited.Contains(next))
					{
						queue.Enqueue(next);
					}
				}
			}
		}

		return false;
	}
}
