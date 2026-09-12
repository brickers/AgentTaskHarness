using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Application.Comments;
using AgentTaskHarness.Application.Features;
using AgentTaskHarness.Application.Steps;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentTaskHarness.Tests;

public class CommentServiceTests : IAsyncLifetime
{
	private readonly SqliteConnection _connection = new("Data Source=:memory:");
	private BoardService _boards = null!;
	private CommentService _commentService = null!;
	private AppDbContext _dbContext = null!;
	private FeatureService _features = null!;
	private StepService _steps = null!;

	public async Task InitializeAsync()
	{
		await _connection.OpenAsync();
		_dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
		await _dbContext.Database.MigrateAsync();
		_boards = new BoardService(_dbContext);
		_features = new FeatureService(_dbContext);
		_steps = new StepService(_dbContext);
		_commentService = new CommentService(_dbContext);
	}

	public async Task DisposeAsync()
	{
		await _dbContext.DisposeAsync();
		await _connection.DisposeAsync();
	}

	[Fact]
	public async Task AddCommentAsync_CreatesCommentForFeature()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");

		var comment = await _commentService.AddCommentAsync(CardType.Feature, feat.Id, "Agent Smith",
			"Initial inspection completed.");

		Assert.NotEqual(Guid.Empty, comment.Id);
		Assert.Equal(CardType.Feature, comment.CardType);
		Assert.Equal(feat.Id, comment.CardId);
		Assert.Equal("Agent Smith", comment.Author);
		Assert.Equal("Initial inspection completed.", comment.Body);
	}

	[Fact]
	public async Task AddCommentAsync_CreatesCommentForStep()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		var comment = await _commentService.AddCommentAsync(CardType.Step, step.Id, "Human Reviewer",
			"Needs better error handling.");

		Assert.NotEqual(Guid.Empty, comment.Id);
		Assert.Equal(CardType.Step, comment.CardType);
		Assert.Equal(step.Id, comment.CardId);
		Assert.Equal("Human Reviewer", comment.Author);
		Assert.Equal("Needs better error handling.", comment.Body);
	}

	[Fact]
	public async Task AddCommentAsync_RejectsInvalidInput()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");

		await Assert.ThrowsAsync<ArgumentException>(() =>
			_commentService.AddCommentAsync(CardType.Feature, feat.Id, "", "Some body"));

		await Assert.ThrowsAsync<ArgumentException>(() =>
			_commentService.AddCommentAsync(CardType.Feature, feat.Id, "   ", "Some body"));

		await Assert.ThrowsAsync<ArgumentException>(() =>
			_commentService.AddCommentAsync(CardType.Feature, feat.Id, new string('a', 201), "Some body"));

		await Assert.ThrowsAsync<ArgumentException>(() =>
			_commentService.AddCommentAsync(CardType.Feature, feat.Id, "Author", ""));

		await Assert.ThrowsAsync<ArgumentException>(() =>
			_commentService.AddCommentAsync(CardType.Feature, feat.Id, "Author", "   "));

		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			_commentService.AddCommentAsync(CardType.Feature, Guid.NewGuid(), "Author", "Body"));

		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			_commentService.AddCommentAsync(CardType.Step, Guid.NewGuid(), "Author", "Body"));
	}

	[Fact]
	public async Task GetCommentsAsync_ReturnsCommentsOrderedByCreatedAt()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");

		var comment1 = await _commentService.AddCommentAsync(CardType.Feature, feat.Id, "Author 1", "First comment");
		comment1.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

		var comment2 = await _commentService.AddCommentAsync(CardType.Feature, feat.Id, "Author 2", "Second comment");
		comment2.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

		var comment3 = await _commentService.AddCommentAsync(CardType.Feature, feat.Id, "Author 3", "Third comment");
		comment3.CreatedAt = DateTimeOffset.UtcNow;

		await _dbContext.SaveChangesAsync();

		var comments = await _commentService.GetCommentsAsync(CardType.Feature, feat.Id);

		Assert.Equal(3, comments.Count);
		Assert.Equal("First comment", comments[0].Body);
		Assert.Equal("Second comment", comments[1].Body);
		Assert.Equal("Third comment", comments[2].Body);
	}

	[Fact]
	public async Task GetCommentCountsForBoardAsync_ReturnsCounts()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat1 = await _features.CreateAsync(board.Id, "Feat 1");
		var feat2 = await _features.CreateAsync(board.Id, "Feat 2");
		var step1 = await _steps.CreateAsync(feat1.Id, "Step 1");

		await _commentService.AddCommentAsync(CardType.Feature, feat1.Id, "Author", "C1");
		await _commentService.AddCommentAsync(CardType.Feature, feat1.Id, "Author", "C2");
		await _commentService.AddCommentAsync(CardType.Step, step1.Id, "Author", "C3");

		var counts = await _commentService.GetCommentCountsForBoardAsync(board.Id);

		Assert.Equal(2, counts[(CardType.Feature, feat1.Id)]);
		Assert.False(counts.ContainsKey((CardType.Feature, feat2.Id)));
		Assert.Equal(1, counts[(CardType.Step, step1.Id)]);
	}

	[Fact]
	public async Task DeleteCommentAsync_RemovesComment()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");

		var comment = await _commentService.AddCommentAsync(CardType.Feature, feat.Id, "Author", "To delete");
		Assert.Equal(1, await _commentService.GetCommentCountAsync(CardType.Feature, feat.Id));

		await _commentService.DeleteCommentAsync(comment.Id);
		Assert.Equal(0, await _commentService.GetCommentCountAsync(CardType.Feature, feat.Id));
	}
}