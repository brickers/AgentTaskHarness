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
	private readonly SqliteConnection _connection = new("Data Source=:memory:");
	private AgentDefinitionService _agentDefinitions = null!;
	private BoardService _boards = null!;
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
		_agentDefinitions = new AgentDefinitionService(_dbContext);
	}

	public async Task DisposeAsync()
	{
		await _dbContext.DisposeAsync();
		await _connection.DisposeAsync();
	}

	[Fact]
	public async Task CreateBoardAsync_SetsReviewTogglesAndThresholds()
	{
		var board = await _boards.CreateAsync(
			"Harness",
			"/repos/harness",
			2,
			true,
			false,
			4,
			2);

		var saved = await _boards.GetByIdAsync(board.Id);
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
			_boards.CreateAsync("Bad", "/repos/bad", 0));

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
			_boards.CreateAsync("Bad", "/repos/bad", agentReviewFailThreshold: -1));

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
			_boards.CreateAsync("Bad", "/repos/bad", humanReviewFailThreshold: -1));
	}

	[Fact]
	public async Task CreateFeatureAsync_DefaultsToBacklogAndRejectsInvalidBoard()
	{
		var board = await _boards.CreateAsync("Harness", "/repos/harness");

		var feature = await _features.CreateAsync(
			board.Id,
			"Feature 1",
			"Reqs",
			"AC",
			"Sol",
			true);

		Assert.Equal(WorkflowColumn.Backlog, feature.WorkflowColumn);
		Assert.True(feature.AlwaysRequireHumanReview);
		Assert.Equal(0, feature.AgentReviewFailCount);
		Assert.Equal(0, feature.HumanReviewFailCount);

		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			_features.CreateAsync(Guid.NewGuid(), "Orphan Feature"));

		await Assert.ThrowsAsync<ArgumentException>(() =>
			_features.CreateAsync(board.Id, "   "));
	}

	[Fact]
	public async Task CreateStepAsync_DefaultsToBacklogAndRejectsInvalidFeature()
	{
		var board = await _boards.CreateAsync("Harness", "/repos/harness");
		var feature = await _features.CreateAsync(board.Id, "Feature 1");

		var step = await _steps.CreateAsync(
			feature.Id,
			"Step 1",
			"Desc",
			"Notes",
			true);

		Assert.Equal(WorkflowColumn.Backlog, step.WorkflowColumn);
		Assert.True(step.AlwaysRequireHumanReview);
		Assert.Equal(0, step.AgentReviewFailCount);
		Assert.Equal(0, step.HumanReviewFailCount);
		Assert.Equal(0, step.TokensUsed);
		Assert.Equal(TimeSpan.Zero, step.TimeSpent);

		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			_steps.CreateAsync(Guid.NewGuid(), "Orphan Step"));

		await Assert.ThrowsAsync<ArgumentException>(() =>
			_steps.CreateAsync(feature.Id, "   "));
	}

	[Fact]
	public async Task GetForBoardAsync_And_GetForFeatureAsync_OrderItemsChronologically()
	{
		var board = await _boards.CreateAsync("Harness", "/repos/harness");
		var feat1 = await _features.CreateAsync(board.Id, "Feature 1");
		var feat2 = await _features.CreateAsync(board.Id, "Feature 2");
		feat1.CreatedAt = new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
		feat2.CreatedAt = new DateTimeOffset(2026, 9, 12, 11, 0, 0, TimeSpan.Zero);
		await _dbContext.SaveChangesAsync();

		var boardFeatures = await _features.GetForBoardAsync(board.Id);
		Assert.Collection(boardFeatures,
			f => Assert.Equal("Feature 1", f.Title),
			f => Assert.Equal("Feature 2", f.Title));

		var step1 = await _steps.CreateAsync(feat1.Id, "Step 1");
		var step2 = await _steps.CreateAsync(feat1.Id, "Step 2");
		step1.CreatedAt = new DateTimeOffset(2026, 9, 12, 10, 5, 0, TimeSpan.Zero);
		step2.CreatedAt = new DateTimeOffset(2026, 9, 12, 10, 10, 0, TimeSpan.Zero);
		await _dbContext.SaveChangesAsync();

		var featureSteps = await _steps.GetForFeatureAsync(feat1.Id);
		Assert.Collection(featureSteps,
			s => Assert.Equal("Step 1", s.Title),
			s => Assert.Equal("Step 2", s.Title));
	}

	[Fact]
	public async Task RoadmapQuery_LoadsFeaturesWithStepsOrderedByCreatedAt_WithoutSqliteDateTimeOffsetError()
	{
		var board = await _boards.CreateAsync("Harness", "/repos/harness");
		var feat1 = await _features.CreateAsync(board.Id, "Feature 1");
		var feat2 = await _features.CreateAsync(board.Id, "Feature 2");
		feat1.CreatedAt = new DateTimeOffset(2026, 9, 12, 11, 0, 0, TimeSpan.Zero);
		feat2.CreatedAt = new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
		await _dbContext.SaveChangesAsync();

		var roadmapFeatures = (await _dbContext.Features
				.AsNoTracking()
				.Include(f => f.Steps)
				.Where(f => f.BoardId == board.Id)
				.ToListAsync())
			.OrderBy(f => f.CreatedAt)
			.ToList();

		Assert.Equal(2, roadmapFeatures.Count);
		Assert.Equal("Feature 2", roadmapFeatures[0].Title);
		Assert.Equal("Feature 1", roadmapFeatures[1].Title);
	}

	[Fact]
	public async Task DeleteFeatureAsync_CascadesToStepsAndRejectsIfAgentRunning()
	{
		var board = await _boards.CreateAsync("Harness", "/repos/harness");
		var feature = await _features.CreateAsync(board.Id, "Feature 1");
		var step = await _steps.CreateAsync(feature.Id, "Step 1");

		// Active agent on step blocks feature deletion
		_dbContext.AgentRuns.Add(new AgentRun
		{
			CardType = CardType.Step,
			CardId = step.Id,
			Status = AgentRunStatus.Working
		});
		await _dbContext.SaveChangesAsync();

		await Assert.ThrowsAsync<InvalidOperationException>(() => _features.DeleteAsync(feature.Id));

		// Remove active run
		var run = await _dbContext.AgentRuns.FirstAsync();
		run.Status = AgentRunStatus.Completed;
		await _dbContext.SaveChangesAsync();

		// Now deletion succeeds and cascades to steps
		await _features.DeleteAsync(feature.Id);
		Assert.Null(await _features.GetByIdAsync(feature.Id));
		Assert.Null(await _steps.GetByIdAsync(step.Id));
	}

	[Fact]
	public async Task DeleteStepAsync_RejectsIfAgentRunning()
	{
		var board = await _boards.CreateAsync("Harness", "/repos/harness");
		var feature = await _features.CreateAsync(board.Id, "Feature 1");
		var step = await _steps.CreateAsync(feature.Id, "Step 1");

		_dbContext.AgentRuns.Add(new AgentRun
		{
			CardType = CardType.Step,
			CardId = step.Id,
			Status = AgentRunStatus.WaitingForInput
		});
		await _dbContext.SaveChangesAsync();

		await Assert.ThrowsAsync<InvalidOperationException>(() => _steps.DeleteAsync(step.Id));

		var run = await _dbContext.AgentRuns.FirstAsync();
		run.Status = AgentRunStatus.Stopped;
		await _dbContext.SaveChangesAsync();

		await _steps.DeleteAsync(step.Id);
		Assert.Null(await _steps.GetByIdAsync(step.Id));
	}

	[Fact]
	public async Task FeatureAndStepDependencies_CanBePersistedAndRetrieved()
	{
		var board = await _boards.CreateAsync("Harness", "/repos/harness");
		var feat1 = await _features.CreateAsync(board.Id, "Feature 1");
		var feat2 = await _features.CreateAsync(board.Id, "Feature 2");

		_dbContext.FeatureDependencies.Add(new FeatureDependency
		{
			FeatureId = feat2.Id,
			DependsOnFeatureId = feat1.Id
		});

		var step1 = await _steps.CreateAsync(feat1.Id, "Step 1");
		var step2 = await _steps.CreateAsync(feat1.Id, "Step 2");

		_dbContext.StepDependencies.Add(new StepDependency
		{
			StepId = step2.Id,
			DependsOnStepId = step1.Id
		});

		await _dbContext.SaveChangesAsync();

		var loadedFeat2 = await _features.GetByIdAsync(feat2.Id);
		Assert.NotNull(loadedFeat2);
		var featDep = Assert.Single(loadedFeat2.Dependencies);
		Assert.Equal(feat1.Id, featDep.DependsOnFeatureId);
		Assert.Equal("Feature 1", featDep.DependsOnFeature.Title);

		var loadedStep2 = await _steps.GetByIdAsync(step2.Id);
		Assert.NotNull(loadedStep2);
		var stepDep = Assert.Single(loadedStep2.Dependencies);
		Assert.Equal(step1.Id, stepDep.DependsOnStepId);
		Assert.Equal("Step 1", stepDep.DependsOnStep.Title);
	}

	[Fact]
	public async Task Comments_CanBePersistedForFeatureAndStep()
	{
		var board = await _boards.CreateAsync("Harness", "/repos/harness");
		var feature = await _features.CreateAsync(board.Id, "Feature 1");
		var step = await _steps.CreateAsync(feature.Id, "Step 1");

		_dbContext.Comments.Add(new Comment
		{
			CardType = CardType.Feature,
			CardId = feature.Id,
			Author = "Dev",
			Body = "Feature comment"
		});
		_dbContext.Comments.Add(new Comment
		{
			CardType = CardType.Step,
			CardId = step.Id,
			Author = "Reviewer",
			Body = "Step comment"
		});
		await _dbContext.SaveChangesAsync();

		var featureComments = await _dbContext.Comments
			.Where(c => c.CardType == CardType.Feature && c.CardId == feature.Id)
			.ToListAsync();
		var stepComments = await _dbContext.Comments
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
		var definition = await _agentDefinitions.CreateAsync("Implementer", "Prompt", "Instructions", "Tools");

		await _agentDefinitions.UpdateAsync(definition.Id, "Reviewer", "Updated prompt", "Updated instructions", "Updated tools");
		var definitions = await _agentDefinitions.GetAllAsync();
		Assert.Single(definitions);
		Assert.Equal("Reviewer", definitions[0].Name);

		await _agentDefinitions.DeleteAsync(definition.Id);
		Assert.Empty(await _agentDefinitions.GetAllAsync());
	}
}