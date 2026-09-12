using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Application.Features;
using AgentTaskHarness.Application.Steps;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Agents;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentTaskHarness.Tests;

public class PersistenceServicesTests : IAsyncLifetime
{
	private readonly SqliteConnection connection = new("Data Source=:memory:");
	private AppDbContext dbContext = null!;
	private BoardService boards = null!;
	private FeatureService features = null!;
	private StepService steps = null!;
	private AgentDefinitionService agentDefinitions = null!;

	public async Task InitializeAsync()
	{
		await connection.OpenAsync();
		dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
		await dbContext.Database.MigrateAsync();
		boards = new BoardService(dbContext);
		features = new FeatureService(dbContext);
		steps = new StepService(dbContext);
		agentDefinitions = new AgentDefinitionService(dbContext, new AgentDefinitionFolderWriter());
	}

	public async Task DisposeAsync()
	{
		await dbContext.DisposeAsync();
		await connection.DisposeAsync();
	}

	[Fact]
	public async Task CreateBoardAsync_SetsReviewTogglesAndThresholds()
	{
		var board = await boards.CreateAsync(
			"Harness",
			"/repos/harness",
			concurrencyLimit: 2,
			skipFeatureHumanReview: true,
			skipStepHumanReview: false,
			agentReviewFailThreshold: 4,
			humanReviewFailThreshold: 2);

		var saved = await boards.GetByIdAsync(board.Id);
		Assert.NotNull(saved);
		Assert.Equal("Harness", saved.Name);
		Assert.Equal("/repos/harness", saved.RepoPath);
		Assert.Equal(2, saved.ConcurrencyLimit);
		Assert.True(saved.SkipFeatureHumanReview);
		Assert.False(saved.SkipStepHumanReview);
		Assert.Equal(4, saved.AgentReviewFailThreshold);
		Assert.Equal(2, saved.HumanReviewFailThreshold);
	}

	[Fact]
	public async Task CreateBoardAsync_RejectsInvalidConstraints()
	{
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
			boards.CreateAsync("Bad", "/repos/bad", concurrencyLimit: 0));

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
			boards.CreateAsync("Bad", "/repos/bad", concurrencyLimit: 1, agentReviewFailThreshold: -1));

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
			boards.CreateAsync("Bad", "/repos/bad", concurrencyLimit: 1, humanReviewFailThreshold: -1));
	}

	[Fact]
	public async Task CreateFeatureAsync_DefaultsToBacklogAndRejectsInvalidBoard()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);

		var feature = await features.CreateAsync(
			board.Id,
			"Feature 1",
			requirements: "Reqs",
			acceptanceCriteria: "AC",
			suggestedSolution: "Sol",
			alwaysRequireHumanReview: true);

		Assert.Equal(WorkflowColumn.Backlog, feature.WorkflowColumn);
		Assert.True(feature.AlwaysRequireHumanReview);
		Assert.Equal(0, feature.AgentReviewFailCount);
		Assert.Equal(0, feature.HumanReviewFailCount);

		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			features.CreateAsync(Guid.NewGuid(), "Orphan Feature"));

		await Assert.ThrowsAsync<ArgumentException>(() =>
			features.CreateAsync(board.Id, "   "));
	}

	[Fact]
	public async Task CreateStepAsync_DefaultsToBacklogAndRejectsInvalidFeature()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var feature = await features.CreateAsync(board.Id, "Feature 1");

		var step = await steps.CreateAsync(
			feature.Id,
			"Step 1",
			description: "Desc",
			guidanceNotes: "Notes",
			alwaysRequireHumanReview: true);

		Assert.Equal(WorkflowColumn.Backlog, step.WorkflowColumn);
		Assert.True(step.AlwaysRequireHumanReview);
		Assert.Equal(0, step.AgentReviewFailCount);
		Assert.Equal(0, step.HumanReviewFailCount);
		Assert.Equal(0, step.TokensUsed);
		Assert.Equal(TimeSpan.Zero, step.TimeSpent);

		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			steps.CreateAsync(Guid.NewGuid(), "Orphan Step"));

		await Assert.ThrowsAsync<ArgumentException>(() =>
			steps.CreateAsync(feature.Id, "   "));
	}

	[Fact]
	public async Task GetForBoardAsync_And_GetForFeatureAsync_OrderItemsChronologically()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var feat1 = await features.CreateAsync(board.Id, "Feature 1");
		var feat2 = await features.CreateAsync(board.Id, "Feature 2");
		feat1.CreatedAt = new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
		feat2.CreatedAt = new DateTimeOffset(2026, 9, 12, 11, 0, 0, TimeSpan.Zero);
		await dbContext.SaveChangesAsync();

		var boardFeatures = await features.GetForBoardAsync(board.Id);
		Assert.Collection(boardFeatures,
			f => Assert.Equal("Feature 1", f.Title),
			f => Assert.Equal("Feature 2", f.Title));

		var step1 = await steps.CreateAsync(feat1.Id, "Step 1");
		var step2 = await steps.CreateAsync(feat1.Id, "Step 2");
		step1.CreatedAt = new DateTimeOffset(2026, 9, 12, 10, 5, 0, TimeSpan.Zero);
		step2.CreatedAt = new DateTimeOffset(2026, 9, 12, 10, 10, 0, TimeSpan.Zero);
		await dbContext.SaveChangesAsync();

		var featureSteps = await steps.GetForFeatureAsync(feat1.Id);
		Assert.Collection(featureSteps,
			s => Assert.Equal("Step 1", s.Title),
			s => Assert.Equal("Step 2", s.Title));
	}

	[Fact]
	public async Task DeleteFeatureAsync_CascadesToStepsAndRejectsIfAgentRunning()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var feature = await features.CreateAsync(board.Id, "Feature 1");
		var step = await steps.CreateAsync(feature.Id, "Step 1");

		// Active agent on step blocks feature deletion
		dbContext.AgentRuns.Add(new AgentRun
		{
			CardType = CardType.Step,
			CardId = step.Id,
			Status = AgentRunStatus.Working
		});
		await dbContext.SaveChangesAsync();

		await Assert.ThrowsAsync<InvalidOperationException>(() => features.DeleteAsync(feature.Id));

		// Remove active run
		var run = await dbContext.AgentRuns.FirstAsync();
		run.Status = AgentRunStatus.Completed;
		await dbContext.SaveChangesAsync();

		// Now deletion succeeds and cascades to steps
		await features.DeleteAsync(feature.Id);
		Assert.Null(await features.GetByIdAsync(feature.Id));
		Assert.Null(await steps.GetByIdAsync(step.Id));
	}

	[Fact]
	public async Task DeleteStepAsync_RejectsIfAgentRunning()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var feature = await features.CreateAsync(board.Id, "Feature 1");
		var step = await steps.CreateAsync(feature.Id, "Step 1");

		dbContext.AgentRuns.Add(new AgentRun
		{
			CardType = CardType.Step,
			CardId = step.Id,
			Status = AgentRunStatus.WaitingForInput
		});
		await dbContext.SaveChangesAsync();

		await Assert.ThrowsAsync<InvalidOperationException>(() => steps.DeleteAsync(step.Id));

		var run = await dbContext.AgentRuns.FirstAsync();
		run.Status = AgentRunStatus.Stopped;
		await dbContext.SaveChangesAsync();

		await steps.DeleteAsync(step.Id);
		Assert.Null(await steps.GetByIdAsync(step.Id));
	}

	[Fact]
	public async Task FeatureAndStepDependencies_CanBePersistedAndRetrieved()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var feat1 = await features.CreateAsync(board.Id, "Feature 1");
		var feat2 = await features.CreateAsync(board.Id, "Feature 2");

		dbContext.FeatureDependencies.Add(new FeatureDependency
		{
			FeatureId = feat2.Id,
			DependsOnFeatureId = feat1.Id
		});

		var step1 = await steps.CreateAsync(feat1.Id, "Step 1");
		var step2 = await steps.CreateAsync(feat1.Id, "Step 2");

		dbContext.StepDependencies.Add(new StepDependency
		{
			StepId = step2.Id,
			DependsOnStepId = step1.Id
		});

		await dbContext.SaveChangesAsync();

		var loadedFeat2 = await features.GetByIdAsync(feat2.Id);
		Assert.NotNull(loadedFeat2);
		var featDep = Assert.Single(loadedFeat2.Dependencies);
		Assert.Equal(feat1.Id, featDep.DependsOnFeatureId);
		Assert.Equal("Feature 1", featDep.DependsOnFeature.Title);

		var loadedStep2 = await steps.GetByIdAsync(step2.Id);
		Assert.NotNull(loadedStep2);
		var stepDep = Assert.Single(loadedStep2.Dependencies);
		Assert.Equal(step1.Id, stepDep.DependsOnStepId);
		Assert.Equal("Step 1", stepDep.DependsOnStep.Title);
	}

	[Fact]
	public async Task Comments_CanBePersistedForFeatureAndStep()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var feature = await features.CreateAsync(board.Id, "Feature 1");
		var step = await steps.CreateAsync(feature.Id, "Step 1");

		dbContext.Comments.Add(new Comment
		{
			CardType = CardType.Feature,
			CardId = feature.Id,
			Author = "Dev",
			Body = "Feature comment"
		});
		dbContext.Comments.Add(new Comment
		{
			CardType = CardType.Step,
			CardId = step.Id,
			Author = "Reviewer",
			Body = "Step comment"
		});
		await dbContext.SaveChangesAsync();

		var featureComments = await dbContext.Comments
			.Where(c => c.CardType == CardType.Feature && c.CardId == feature.Id)
			.ToListAsync();
		var stepComments = await dbContext.Comments
			.Where(c => c.CardType == CardType.Step && c.CardId == step.Id)
			.ToListAsync();

		Assert.Single(featureComments);
		Assert.Equal("Feature comment", featureComments[0].Body);
		Assert.Single(stepComments);
		Assert.Equal("Step comment", stepComments[0].Body);
	}

	[Fact]
	public async Task AgentDefinitionAsync_SupportsCreateUpdateAndDelete()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var definition = await agentDefinitions.CreateAsync(board.Id, "Implementer", "/agents/implementer");

		await agentDefinitions.UpdateAsync(definition.Id, "Reviewer", "/agents/reviewer");
		var boardDefinitions = await agentDefinitions.GetForBoardAsync(board.Id);
		Assert.Single(boardDefinitions);
		Assert.Equal("Reviewer", boardDefinitions[0].Name);

		await agentDefinitions.DeleteAsync(definition.Id);
		Assert.Empty(await agentDefinitions.GetForBoardAsync(board.Id));
	}
}
