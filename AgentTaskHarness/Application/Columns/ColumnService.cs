using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Columns;

public class ColumnService(AppDbContext dbContext)
{
	public async Task<Column> CreateAsync(Guid boardId, string name, int order, bool isBacklog = false, bool isTerminal = false, Guid? agentDefinitionId = null, CancellationToken cancellationToken = default)
	{
		await ValidateAsync(boardId, name, order, isBacklog, isTerminal, agentDefinitionId, null, cancellationToken);
		var column = new Column { BoardId = boardId, Name = name.Trim(), Order = order, IsBacklog = isBacklog, IsTerminal = isTerminal, AgentDefinitionId = agentDefinitionId };
		dbContext.Columns.Add(column);
		await dbContext.SaveChangesAsync(cancellationToken);
		return column;
	}

	public Task<List<Column>> GetForBoardAsync(Guid boardId, CancellationToken cancellationToken = default) =>
		dbContext.Columns.AsNoTracking().Where(column => column.BoardId == boardId).OrderBy(column => column.Order).ToListAsync(cancellationToken);

	public async Task<Column> UpdateAsync(Guid columnId, string name, int order, bool isBacklog, bool isTerminal, Guid? agentDefinitionId, CancellationToken cancellationToken = default)
	{
		var column = await FindColumnAsync(columnId, cancellationToken);
		if (column.IsBacklog && !isBacklog)
		{
			throw new InvalidOperationException("Every board must retain a backlog column.");
		}
		if (column.IsTerminal && !isTerminal)
		{
			throw new InvalidOperationException("Every board must retain a terminal column.");
		}
		await ValidateAsync(column.BoardId, name, order, isBacklog, isTerminal, agentDefinitionId, columnId, cancellationToken);
		column.Name = name.Trim();
		column.Order = order;
		column.IsBacklog = isBacklog;
		column.IsTerminal = isTerminal;
		column.AgentDefinitionId = agentDefinitionId;
		await dbContext.SaveChangesAsync(cancellationToken);
		return column;
	}

	public async Task ReorderAsync(Guid boardId, IReadOnlyList<Guid> columnIds, CancellationToken cancellationToken = default)
	{
		var boardColumns = await dbContext.Columns.Where(column => column.BoardId == boardId).OrderBy(column => column.Order).ToListAsync(cancellationToken);
		if (boardColumns.Count != columnIds.Count || boardColumns.Select(column => column.Id).Except(columnIds).Any() || columnIds.Distinct().Count() != columnIds.Count)
		{
			throw new InvalidOperationException("The reordered columns must exactly match the board's columns.");
		}

		var positions = columnIds.Select((columnId, index) => new { columnId, index }).ToDictionary(item => item.columnId, item => item.index);
		foreach (var column in boardColumns)
		{
			column.Order = -positions[column.Id] - 1;
		}
		await dbContext.SaveChangesAsync(cancellationToken);

		foreach (var column in boardColumns)
		{
			column.Order = positions[column.Id];
		}
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public async Task DeleteAsync(Guid columnId, CancellationToken cancellationToken = default)
	{
		var column = await FindColumnAsync(columnId, cancellationToken);
		if (column.IsBacklog || column.IsTerminal)
		{
			throw new InvalidOperationException("The board backlog and terminal columns cannot be deleted.");
		}
		if (await dbContext.Tasks.AnyAsync(task => task.ColumnId == columnId, cancellationToken))
		{
			throw new InvalidOperationException("A column containing tasks cannot be deleted.");
		}

		dbContext.ColumnTransitions.RemoveRange(dbContext.ColumnTransitions.Where(transition => transition.FromColumnId == columnId || transition.ToColumnId == columnId));
		dbContext.Columns.Remove(column);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public async Task AddAllowedTransitionAsync(Guid fromColumnId, Guid toColumnId, CancellationToken cancellationToken = default)
	{
		if (fromColumnId == toColumnId)
		{
			throw new InvalidOperationException("A column cannot transition to itself.");
		}
		var fromColumn = await FindColumnAsync(fromColumnId, cancellationToken);
		var toColumn = await FindColumnAsync(toColumnId, cancellationToken);
		if (fromColumn.BoardId != toColumn.BoardId)
		{
			throw new InvalidOperationException("Allowed transitions must remain within the same board.");
		}
		if (await dbContext.ColumnTransitions.AnyAsync(transition => transition.FromColumnId == fromColumnId && transition.ToColumnId == toColumnId, cancellationToken))
		{
			return;
		}

		dbContext.ColumnTransitions.Add(new ColumnTransition { FromColumnId = fromColumnId, ToColumnId = toColumnId });
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public async Task RemoveAllowedTransitionAsync(Guid fromColumnId, Guid toColumnId, CancellationToken cancellationToken = default)
	{
		var transition = await dbContext.ColumnTransitions.SingleOrDefaultAsync(item => item.FromColumnId == fromColumnId && item.ToColumnId == toColumnId, cancellationToken);
		if (transition is null)
		{
			return;
		}
		dbContext.ColumnTransitions.Remove(transition);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public Task<List<Column>> GetAllowedTargetsAsync(Guid fromColumnId, CancellationToken cancellationToken = default) =>
		dbContext.ColumnTransitions.AsNoTracking()
			.Where(transition => transition.FromColumnId == fromColumnId)
			.Select(transition => transition.ToColumn)
			.OrderBy(column => column.Order)
			.ToListAsync(cancellationToken);

	private async Task<Column> FindColumnAsync(Guid columnId, CancellationToken cancellationToken) =>
		await dbContext.Columns.SingleOrDefaultAsync(column => column.Id == columnId, cancellationToken)
		?? throw new KeyNotFoundException($"Column '{columnId}' was not found.");

	private async Task ValidateAsync(Guid boardId, string name, int order, bool isBacklog, bool isTerminal, Guid? agentDefinitionId, Guid? currentColumnId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		if (order < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(order), "Column order cannot be negative.");
		}
		if (isBacklog && isTerminal)
		{
			throw new InvalidOperationException("A column cannot be both backlog and terminal.");
		}
		if ((isBacklog || isTerminal) && agentDefinitionId is not null)
		{
			throw new InvalidOperationException("Backlog and terminal columns cannot have an agent definition.");
		}
		if (!await dbContext.Boards.AnyAsync(board => board.Id == boardId, cancellationToken))
		{
			throw new KeyNotFoundException($"Board '{boardId}' was not found.");
		}
		if (await dbContext.Columns.AnyAsync(column => column.BoardId == boardId && column.Order == order && column.Id != currentColumnId, cancellationToken))
		{
			throw new InvalidOperationException("Column order must be unique within a board.");
		}
		if (isBacklog && await dbContext.Columns.AnyAsync(column => column.BoardId == boardId && column.IsBacklog && column.Id != currentColumnId, cancellationToken))
		{
			throw new InvalidOperationException("A board can have only one backlog column.");
		}
		if (isTerminal && await dbContext.Columns.AnyAsync(column => column.BoardId == boardId && column.IsTerminal && column.Id != currentColumnId, cancellationToken))
		{
			throw new InvalidOperationException("A board can have only one terminal column.");
		}
		if (agentDefinitionId is not null && !await dbContext.AgentDefinitions.AnyAsync(definition => definition.Id == agentDefinitionId && definition.BoardId == boardId, cancellationToken))
		{
			throw new InvalidOperationException("The agent definition must belong to the same board as the column.");
		}
	}
}