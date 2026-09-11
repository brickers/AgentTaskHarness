using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Boards;

public class BoardService(AppDbContext dbContext)
{
	public async Task<Board> CreateAsync(string name, string repoPath, int concurrencyLimit, CancellationToken cancellationToken = default)
	{
		Validate(name, repoPath, concurrencyLimit);

		var board = new Board
		{
			Name = name.Trim(),
			RepoPath = repoPath.Trim(),
			ConcurrencyLimit = concurrencyLimit
		};
		board.Columns.Add(new Column { Name = "Backlog", Order = 0, IsBacklog = true });
		board.Columns.Add(new Column { Name = "Done", Order = 1, IsTerminal = true });

		dbContext.Boards.Add(board);
		await dbContext.SaveChangesAsync(cancellationToken);
		return board;
	}

	public Task<List<Board>> GetAllAsync(CancellationToken cancellationToken = default) =>
		dbContext.Boards.AsNoTracking().OrderBy(board => board.Name).ToListAsync(cancellationToken);

	public Task<Board?> GetByIdAsync(Guid boardId, CancellationToken cancellationToken = default) =>
		dbContext.Boards.Include(board => board.Columns.OrderBy(column => column.Order)).SingleOrDefaultAsync(board => board.Id == boardId, cancellationToken);

	public async Task<Board> UpdateAsync(Guid boardId, string name, string repoPath, int concurrencyLimit, CancellationToken cancellationToken = default)
	{
		Validate(name, repoPath, concurrencyLimit);
		var board = await FindBoardAsync(boardId, cancellationToken);
		board.Name = name.Trim();
		board.RepoPath = repoPath.Trim();
		board.ConcurrencyLimit = concurrencyLimit;
		await dbContext.SaveChangesAsync(cancellationToken);
		return board;
	}

	public async Task DeleteAsync(Guid boardId, CancellationToken cancellationToken = default)
	{
		var board = await FindBoardAsync(boardId, cancellationToken);
		dbContext.Boards.Remove(board);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	private async Task<Board> FindBoardAsync(Guid boardId, CancellationToken cancellationToken) =>
		await dbContext.Boards.SingleOrDefaultAsync(board => board.Id == boardId, cancellationToken)
		?? throw new KeyNotFoundException($"Board '{boardId}' was not found.");

	private static void Validate(string name, string repoPath, int concurrencyLimit)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
		if (concurrencyLimit < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(concurrencyLimit), "Concurrency limit must be at least one.");
		}
	}
}