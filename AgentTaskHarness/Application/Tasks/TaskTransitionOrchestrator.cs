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
		return await MoveTaskAsync(taskId, targetColumnId, false, cancellationToken);
	}

	public async Task<TaskItem> MoveTaskAndDiscardWorktreeAsync(Guid taskId, Guid targetColumnId, CancellationToken cancellationToken = default)
	{
		return await MoveTaskAsync(taskId, targetColumnId, true, cancellationToken);
	}

	public async Task<TaskItem> ResumeMergeAsync(Guid taskId, CancellationToken cancellationToken = default)
	{
		var task = await dbContext.Tasks.SingleOrDefaultAsync(item => item.Id == taskId, cancellationToken)
			?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
		if (!task.MergeConflictPending)
		{
			throw new InvalidOperationException("This task does not have a merge conflict pending.");
		}

		var doneColumn = await dbContext.Columns.SingleOrDefaultAsync(column => column.BoardId == task.BoardId && column.IsTerminal, cancellationToken)
			?? throw new InvalidOperationException("The task board does not have a terminal column.");
		await gitWorktrees.ResumeMergeAsync(task, cancellationToken);
		task.ColumnId = doneColumn.Id;
		task.Status = DomainTaskStatus.Completed;
		task.MergeConflictPending = false;
		await dbContext.SaveChangesAsync(cancellationToken);
		return task;
	}

	public async Task DiscardUncommittedChangesAsync(Guid taskId, CancellationToken cancellationToken = default)
	{
		var task = await dbContext.Tasks.SingleOrDefaultAsync(item => item.Id == taskId, cancellationToken)
			?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
		await gitWorktrees.DiscardUncommittedChangesAsync(task, cancellationToken);
	}

	private async Task<TaskItem> MoveTaskAsync(Guid taskId, Guid targetColumnId, bool discardWorktreeOnBacklogReturn, CancellationToken cancellationToken)
	{
		var task = await dbContext.Tasks.Include(item => item.Dependencies).SingleOrDefaultAsync(item => item.Id == taskId, cancellationToken)
			?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
		var sourceColumn = await FindColumnAsync(task.ColumnId, cancellationToken);
		var targetColumn = await FindColumnAsync(targetColumnId, cancellationToken);

		if (task.Status == DomainTaskStatus.Completed)
		{
			throw new InvalidOperationException("Completed tasks are terminal and cannot be moved.");
		}
		if (task.MergeConflictPending)
		{
			throw new InvalidOperationException("Resolve the pending merge conflict before moving this task.");
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

		try
		{
			if (targetColumn.IsBacklog && discardWorktreeOnBacklogReturn)
			{
				await gitWorktrees.DiscardWorktreeAsync(task, cancellationToken);
			}
			else
			{
				await gitWorktrees.HandleTransitionAsync(task, sourceColumn, targetColumn, cancellationToken);
			}
		}
		catch (GitMergeConflictException)
		{
			task.MergeConflictPending = true;
			await dbContext.SaveChangesAsync(cancellationToken);
			throw;
		}
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