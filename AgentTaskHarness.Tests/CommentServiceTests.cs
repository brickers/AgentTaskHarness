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
	private readonly SqliteConnection connection = new("Data Source=:memory:");
	private AppDbContext dbContext = null!;
	private BoardService boards = null!;
	private FeatureService features = null!;
	private StepService steps = null!;
	private CommentService commentService = null!;

	public async Task InitializeAsync()
	{
		await connection.OpenAsync();
		dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
		await dbContext.Database.MigrateAsync();
		boards = new BoardService(dbContext);
		features = new FeatureService(dbContext);
		steps = new StepService(dbContext);
		commentService = new CommentService(dbContext);
	}

	public async Task DisposeAsync()
	{
		await dbContext.DisposeAsync();
		await connection.DisposeAsync();
	}

	[Fact]
	public async Task AddCommentAsync_CreatesCommentForFeature()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");

		var comment = await commentService.AddCommentAsync(CardType.Feature, feat.Id, "Agent Smith", "Initial inspection completed.");

		Assert.NotEqual(Guid.Empty, comment.Id);
		Assert.Equal(CardType.Feature, comment.CardType);
		Assert.Equal(feat.Id, comment.CardId);
		Assert.Equal("Agent Smith", comment.Author);
		Assert.Equal("Initial inspection completed.", comment.Body);
	}

	[Fact]
	public async Task AddCommentAsync_CreatesCommentForStep()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		var comment = await commentService.AddCommentAsync(CardType.Step, step.Id, "Human Reviewer", "Needs better error handling.");

		Assert.NotEqual(Guid.Empty, comment.Id);
		Assert.Equal(CardType.Step, comment.CardType);
		Assert.Equal(step.Id, comment.CardId);
		Assert.Equal("Human Reviewer", comment.Author);
		Assert.Equal("Needs better error handling.", comment.Body);
	}

	[Fact]
	public async Task AddCommentAsync_RejectsInvalidInput()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");

		await Assert.ThrowsAsync<ArgumentException>(() =>
			commentService.AddCommentAsync(CardType.Feature, feat.Id, "", "Some body"));

		await Assert.ThrowsAsync<ArgumentException>(() =>
			commentService.AddCommentAsync(CardType.Feature, feat.Id, "   ", "Some body"));

		await Assert.ThrowsAsync<ArgumentException>(() =>
			commentService.AddCommentAsync(CardType.Feature, feat.Id, new string('a', 201), "Some body"));

		await Assert.ThrowsAsync<ArgumentException>(() =>
			commentService.AddCommentAsync(CardType.Feature, feat.Id, "Author", ""));

		await Assert.ThrowsAsync<ArgumentException>(() =>
			commentService.AddCommentAsync(CardType.Feature, feat.Id, "Author", "   "));

		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			commentService.AddCommentAsync(CardType.Feature, Guid.NewGuid(), "Author", "Body"));

		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			commentService.AddCommentAsync(CardType.Step, Guid.NewGuid(), "Author", "Body"));
	}

	[Fact]
	public async Task GetCommentsAsync_ReturnsCommentsOrderedByCreatedAt()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");

		var comment1 = await commentService.AddCommentAsync(CardType.Feature, feat.Id, "Author 1", "First comment");
		comment1.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

		var comment2 = await commentService.AddCommentAsync(CardType.Feature, feat.Id, "Author 2", "Second comment");
		comment2.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

		var comment3 = await commentService.AddCommentAsync(CardType.Feature, feat.Id, "Author 3", "Third comment");
		comment3.CreatedAt = DateTimeOffset.UtcNow;

		await dbContext.SaveChangesAsync();

		var comments = await commentService.GetCommentsAsync(CardType.Feature, feat.Id);

		Assert.Equal(3, comments.Count);
		Assert.Equal("First comment", comments[0].Body);
		Assert.Equal("Second comment", comments[1].Body);
		Assert.Equal("Third comment", comments[2].Body);
	}

	[Fact]
	public async Task GetCommentCountsForBoardAsync_ReturnsCounts()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat1 = await features.CreateAsync(board.Id, "Feat 1");
		var feat2 = await features.CreateAsync(board.Id, "Feat 2");
		var step1 = await steps.CreateAsync(feat1.Id, "Step 1");

		await commentService.AddCommentAsync(CardType.Feature, feat1.Id, "Author", "C1");
		await commentService.AddCommentAsync(CardType.Feature, feat1.Id, "Author", "C2");
		await commentService.AddCommentAsync(CardType.Step, step1.Id, "Author", "C3");

		var counts = await commentService.GetCommentCountsForBoardAsync(board.Id);

		Assert.Equal(2, counts[(CardType.Feature, feat1.Id)]);
		Assert.False(counts.ContainsKey((CardType.Feature, feat2.Id)));
		Assert.Equal(1, counts[(CardType.Step, step1.Id)]);
	}

	[Fact]
	public async Task DeleteCommentAsync_RemovesComment()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");

		var comment = await commentService.AddCommentAsync(CardType.Feature, feat.Id, "Author", "To delete");
		Assert.Equal(1, await commentService.GetCommentCountAsync(CardType.Feature, feat.Id));

		await commentService.DeleteCommentAsync(comment.Id);
		Assert.Equal(0, await commentService.GetCommentCountAsync(CardType.Feature, feat.Id));
	}
}
