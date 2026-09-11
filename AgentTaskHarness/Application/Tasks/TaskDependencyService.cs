using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Tasks;

public class TaskDependencyService(AppDbContext dbContext)
{
	public async Task AddAsync(Guid taskId, Guid dependsOnTaskId, CancellationToken cancellationToken = default)
	{
		if (taskId == dependsOnTaskId)
		{
			throw new InvalidOperationException("A task cannot depend on itself.");
		}
		var task = await FindTaskAsync(taskId, cancellationToken);
		var dependency = await FindTaskAsync(dependsOnTaskId, cancellationToken);
		if (task.BoardId != dependency.BoardId)
		{
			throw new InvalidOperationException("Task dependencies must remain within the same board.");
		}
		if (await dbContext.TaskDependencies.AnyAsync(item => item.TaskId == taskId && item.DependsOnTaskId == dependsOnTaskId, cancellationToken))
		{
			return;
		}

		dbContext.TaskDependencies.Add(new TaskDependency { TaskId = taskId, DependsOnTaskId = dependsOnTaskId });
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public async Task RemoveAsync(Guid taskId, Guid dependsOnTaskId, CancellationToken cancellationToken = default)
	{
		var dependency = await dbContext.TaskDependencies.SingleOrDefaultAsync(item => item.TaskId == taskId && item.DependsOnTaskId == dependsOnTaskId, cancellationToken);
		if (dependency is null)
		{
			return;
		}
		dbContext.TaskDependencies.Remove(dependency);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public Task<List<TaskDependency>> GetForTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
		dbContext.TaskDependencies.AsNoTracking().Where(dependency => dependency.TaskId == taskId).ToListAsync(cancellationToken);

	private async Task<TaskItem> FindTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
		await dbContext.Tasks.SingleOrDefaultAsync(task => task.Id == taskId, cancellationToken)
		?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
}