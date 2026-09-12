using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Features;

public class FeatureDependencyService(AppDbContext dbContext)
{
	public async Task AddAsync(Guid featureId, Guid dependsOnFeatureId, CancellationToken cancellationToken = default)
	{
		if (featureId == dependsOnFeatureId) throw new InvalidOperationException("A feature cannot depend on itself.");

		var feature = await FindFeatureAsync(featureId, cancellationToken);
		EnsureDependenciesAreEditable(feature);

		var dependsOn = await FindFeatureAsync(dependsOnFeatureId, cancellationToken);
		if (feature.BoardId != dependsOn.BoardId)
			throw new InvalidOperationException("Feature dependencies must remain within the same board.");

		if (await dbContext.FeatureDependencies.AnyAsync(
			    d => d.FeatureId == featureId && d.DependsOnFeatureId == dependsOnFeatureId, cancellationToken)) return;

		if (await WouldCreateCycleAsync(featureId, dependsOnFeatureId, cancellationToken))
			throw new InvalidOperationException("Circular dependencies are not allowed.");

		dbContext.FeatureDependencies.Add(new FeatureDependency
		{
			FeatureId = featureId,
			DependsOnFeatureId = dependsOnFeatureId
		});
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public async Task RemoveAsync(Guid featureId, Guid dependsOnFeatureId,
		CancellationToken cancellationToken = default)
	{
		var feature = await FindFeatureAsync(featureId, cancellationToken);
		EnsureDependenciesAreEditable(feature);

		var dependency = await dbContext.FeatureDependencies.SingleOrDefaultAsync(
			d => d.FeatureId == featureId && d.DependsOnFeatureId == dependsOnFeatureId,
			cancellationToken);

		if (dependency is null) return;

		dbContext.FeatureDependencies.Remove(dependency);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public Task<List<FeatureDependency>> GetForFeatureAsync(Guid featureId,
		CancellationToken cancellationToken = default)
	{
		return dbContext.FeatureDependencies
			.AsNoTracking()
			.Include(d => d.DependsOnFeature)
			.Where(d => d.FeatureId == featureId)
			.ToListAsync(cancellationToken);
	}

	public async Task<bool> AreDependenciesMetAsync(Guid featureId, CancellationToken cancellationToken = default)
	{
		var hasUnfinished = await dbContext.FeatureDependencies
			.Where(d => d.FeatureId == featureId)
			.AnyAsync(d => d.DependsOnFeature.WorkflowColumn != WorkflowColumn.Done, cancellationToken);

		return !hasUnfinished;
	}

	public Task<List<Feature>> GetUnmetDependenciesAsync(Guid featureId, CancellationToken cancellationToken = default)
	{
		return dbContext.FeatureDependencies
			.AsNoTracking()
			.Where(d => d.FeatureId == featureId && d.DependsOnFeature.WorkflowColumn != WorkflowColumn.Done)
			.Select(d => d.DependsOnFeature)
			.ToListAsync(cancellationToken);
	}

	private async Task<Feature> FindFeatureAsync(Guid featureId, CancellationToken cancellationToken)
	{
		return await dbContext.Features.SingleOrDefaultAsync(f => f.Id == featureId, cancellationToken)
		       ?? throw new KeyNotFoundException($"Feature '{featureId}' was not found.");
	}

	private static void EnsureDependenciesAreEditable(Feature feature)
	{
		if (feature.WorkflowColumn != WorkflowColumn.Backlog)
			throw new InvalidOperationException(
				"Feature dependencies can only be changed while the feature is in the backlog.");
	}

	private async Task<bool> WouldCreateCycleAsync(Guid featureId, Guid dependsOnFeatureId,
		CancellationToken cancellationToken)
	{
		var visited = new HashSet<Guid>();
		var queue = new Queue<Guid>();
		queue.Enqueue(dependsOnFeatureId);

		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			if (current == featureId) return true;

			if (visited.Add(current))
			{
				var nextDependencies = await dbContext.FeatureDependencies
					.Where(d => d.FeatureId == current)
					.Select(d => d.DependsOnFeatureId)
					.ToListAsync(cancellationToken);

				foreach (var next in nextDependencies)
					if (!visited.Contains(next))
						queue.Enqueue(next);
			}
		}

		return false;
	}
}