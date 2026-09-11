using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using DomainTaskStatus = AgentTaskHarness.Domain.Enums.TaskStatus;

namespace AgentTaskHarness.Application.Tasks;

public class TaskService(AppDbContext dbContext)
{
	public async Task<TaskItem> CreateAsync(Guid boardId, Guid backlogColumnId, string title, string description, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(title);
		var backlogColumn = await dbContext.Columns.SingleOrDefaultAsync(column => column.Id == backlogColumnId, cancellationToken)
			?? throw new KeyNotFoundException($"Column '{backlogColumnId}' was not found.");
		if (backlogColumn.BoardId != boardId || !backlogColumn.IsBacklog)
		{
			throw new InvalidOperationException("Tasks can only be created in their board's backlog column.");
		}

		var task = new TaskItem { BoardId = boardId, ColumnId = backlogColumnId, Title = title.Trim(), Description = description, Status = DomainTaskStatus.Backlog };
		dbContext.Tasks.Add(task);
		await dbContext.SaveChangesAsync(cancellationToken);
		return task;
	}

	public async Task<List<TaskItem>> GetForBoardAsync(Guid boardId, CancellationToken cancellationToken = default)
	{
		var boardTasks = await dbContext.Tasks.AsNoTracking().Where(task => task.BoardId == boardId).ToListAsync(cancellationToken);
		return boardTasks.OrderBy(task => task.CreatedAt).ToList();
	}

	public Task<TaskItem?> GetByIdAsync(Guid taskId, CancellationToken cancellationToken = default) =>
		dbContext.Tasks.Include(task => task.Dependencies).SingleOrDefaultAsync(task => task.Id == taskId, cancellationToken);

	public async Task<TaskItem> UpdateAsync(Guid taskId, string title, string description, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(title);
		var task = await FindTaskAsync(taskId, cancellationToken);
		task.Title = title.Trim();
		task.Description = description;
		await dbContext.SaveChangesAsync(cancellationToken);
		return task;
	}

	public async Task DeleteAsync(Guid taskId, CancellationToken cancellationToken = default)
	{
		var task = await FindTaskAsync(taskId, cancellationToken);
		if (await dbContext.AgentRuns.AnyAsync(run => run.TaskId == taskId && (run.Status == AgentRunStatus.Working || run.Status == AgentRunStatus.WaitingForInput), cancellationToken))
		{
			throw new InvalidOperationException("A task with a running agent cannot be deleted.");
		}
		dbContext.TaskDependencies.RemoveRange(dbContext.TaskDependencies.Where(dependency => dependency.TaskId == taskId || dependency.DependsOnTaskId == taskId));
		dbContext.Tasks.Remove(task);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	private async Task<TaskItem> FindTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
		await dbContext.Tasks.SingleOrDefaultAsync(task => task.Id == taskId, cancellationToken)
		?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
}