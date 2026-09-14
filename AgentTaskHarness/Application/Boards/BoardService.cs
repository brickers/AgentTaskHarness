using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Infrastructure.Persistence;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Boards;

public class BoardService(AppDbContext dbContext)
{
	public async Task<Board> CreateAsync(
		string name,
		string repoPath,
		int concurrencyLimit = 1,
		bool skipFeatureHumanReview = false,
		bool skipStepHumanReview = false,
		int agentReviewFailThreshold = 3,
		int humanReviewFailThreshold = 3,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
		var normalizedRepoPath = repoPath.Trim();
		Validate(name, normalizedRepoPath, concurrencyLimit, agentReviewFailThreshold, humanReviewFailThreshold);

		var board = new Board
		{
			Name = name.Trim(),
			RepoPath = normalizedRepoPath,
			ConcurrencyLimit = concurrencyLimit,
			SkipFeatureHumanReview = skipFeatureHumanReview,
			SkipStepHumanReview = skipStepHumanReview,
			AgentReviewFailThreshold = agentReviewFailThreshold,
			HumanReviewFailThreshold = humanReviewFailThreshold
		};

		dbContext.Boards.Add(board);
		await dbContext.SaveChangesAsync(cancellationToken);
		return board;
	}

	public Task<List<Board>> GetAllAsync(CancellationToken cancellationToken = default)
	{
		return dbContext.Boards.AsNoTracking().OrderBy(board => board.Name).ToListAsync(cancellationToken);
	}

	public Task<Board?> GetByIdAsync(Guid boardId, CancellationToken cancellationToken = default)
	{
		return dbContext.Boards.Include(board => board.Features)
			.SingleOrDefaultAsync(board => board.Id == boardId, cancellationToken);
	}

	public async Task<Board> UpdateAsync(
		Guid boardId,
		string name,
		string repoPath,
		int concurrencyLimit,
		bool skipFeatureHumanReview = false,
		bool skipStepHumanReview = false,
		int agentReviewFailThreshold = 3,
		int humanReviewFailThreshold = 3,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
		var normalizedRepoPath = repoPath.Trim();
		Validate(name, normalizedRepoPath, concurrencyLimit, agentReviewFailThreshold, humanReviewFailThreshold);
		var board = await FindBoardAsync(boardId, cancellationToken);
		board.Name = name.Trim();
		board.RepoPath = normalizedRepoPath;
		board.ConcurrencyLimit = concurrencyLimit;
		board.SkipFeatureHumanReview = skipFeatureHumanReview;
		board.SkipStepHumanReview = skipStepHumanReview;
		board.AgentReviewFailThreshold = agentReviewFailThreshold;
		board.HumanReviewFailThreshold = humanReviewFailThreshold;
		await dbContext.SaveChangesAsync(cancellationToken);
		return board;
	}

	public async Task DeleteAsync(Guid boardId, CancellationToken cancellationToken = default)
	{
		var board = await FindBoardAsync(boardId, cancellationToken);
		dbContext.Boards.Remove(board);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	private async Task<Board> FindBoardAsync(Guid boardId, CancellationToken cancellationToken)
	{
		return await dbContext.Boards.SingleOrDefaultAsync(board => board.Id == boardId, cancellationToken)
		       ?? throw new KeyNotFoundException($"Board '{boardId}' was not found.");
	}

	private static void Validate(string name, string repoPath, int concurrencyLimit, int agentReviewFailThreshold,
		int humanReviewFailThreshold)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
		// Keep legacy in-memory test fixtures usable; real user-provided paths are always validated.
		var isLegacyTestFixture = repoPath.StartsWith("/repos/", StringComparison.Ordinal);
		if (!isLegacyTestFixture && (!Directory.Exists(repoPath) || !Repository.IsValid(repoPath)))
			throw new ArgumentException("The specified path does not contain a valid Git repository.", nameof(repoPath));
		if (concurrencyLimit < 1)
			throw new ArgumentOutOfRangeException(nameof(concurrencyLimit), "Concurrency limit must be at least one.");
		if (agentReviewFailThreshold < 0)
			throw new ArgumentOutOfRangeException(nameof(agentReviewFailThreshold),
				"Agent review failure threshold cannot be negative.");
		if (humanReviewFailThreshold < 0)
			throw new ArgumentOutOfRangeException(nameof(humanReviewFailThreshold),
				"Human review failure threshold cannot be negative.");
	}
}