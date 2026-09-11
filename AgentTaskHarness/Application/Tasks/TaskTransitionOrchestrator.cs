using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using DomainTaskStatus = AgentTaskHarness.Domain.Enums.TaskStatus;

namespace AgentTaskHarness.Application.Tasks;

public class TaskTransitionOrchestrator(AppDbContext dbContext, IGitWorktreeService gitWorktrees, IAgentScheduler scheduler)
{
	public async Task<TaskItem> MoveTaskAsync(Guid taskId, Guid targetColumnId, CancellationToken cancellationToken = default)
	{
		var task = await dbContext.Tasks.Include(item => item.Dependencies).SingleOrDefaultAsync(item => item.Id == taskId, cancellationToken)
			?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
		var sourceColumn = await FindColumnAsync(task.ColumnId, cancellationToken);
		var targetColumn = await FindColumnAsync(targetColumnId, cancellationToken);

		if (task.Status == DomainTaskStatus.Completed)
		{
			throw new InvalidOperationException("Completed tasks are terminal and cannot be moved.");
		}
		if (targetColumn.BoardId != task.BoardId)
		{
			throw new InvalidOperationException("Tasks can only move to columns on their own board.");
		}
		if (!await dbContext.ColumnTransitions.AnyAsync(transition => transition.FromColumnId == sourceColumn.Id && transition.ToColumnId == targetColumn.Id, cancellationToken))
		{
			throw new InvalidOperationException($"Moving from '{sourceColumn.Name}' to '{targetColumn.Name}' is not allowed.");
		}
		if (sourceColumn.IsBacklog && !targetColumn.IsBacklog)
		{
			var hasUnfinishedDependencies = await dbContext.TaskDependencies
				.Where(dependency => dependency.TaskId == taskId)
				.AnyAsync(dependency => dependency.DependsOnTask.Status != DomainTaskStatus.Completed, cancellationToken);
			if (hasUnfinishedDependencies)
			{
				throw new InvalidOperationException("All task dependencies must be completed before leaving the backlog.");
			}
		}

		await gitWorktrees.HandleTransitionAsync(task, sourceColumn, targetColumn, cancellationToken);
		task.ColumnId = targetColumn.Id;
		task.Status = targetColumn.IsBacklog ? DomainTaskStatus.Backlog : targetColumn.IsTerminal ? DomainTaskStatus.Completed : DomainTaskStatus.InProgress;
		await dbContext.SaveChangesAsync(cancellationToken);

		if (targetColumn.AgentDefinitionId is not null)
		{
			await scheduler.RequestStartAsync(task, targetColumn, cancellationToken);
		}

		return task;
	}

	private async Task<Column> FindColumnAsync(Guid columnId, CancellationToken cancellationToken) =>
		await dbContext.Columns.SingleOrDefaultAsync(column => column.Id == columnId, cancellationToken)
		?? throw new KeyNotFoundException($"Column '{columnId}' was not found.");
}