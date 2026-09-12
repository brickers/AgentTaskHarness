using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Application.Features;
using AgentTaskHarness.Application.Steps;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentTaskHarness.Tests;

public class DependencyServicesTests : IAsyncLifetime
{
	private readonly SqliteConnection _connection = new("Data Source=:memory:");
	private BoardService _boards = null!;
	private AppDbContext _dbContext = null!;
	private FeatureDependencyService _featureDeps = null!;
	private FeatureService _features = null!;
	private StepDependencyService _stepDeps = null!;
	private StepService _steps = null!;

	public async Task InitializeAsync()
	{
		await _connection.OpenAsync();
		_dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
		await _dbContext.Database.MigrateAsync();
		_boards = new BoardService(_dbContext);
		_features = new FeatureService(_dbContext);
		_steps = new StepService(_dbContext);
		_featureDeps = new FeatureDependencyService(_dbContext);
		_stepDeps = new StepDependencyService(_dbContext);
	}

	public async Task DisposeAsync()
	{
		await _dbContext.DisposeAsync();
		await _connection.DisposeAsync();
	}

	[Fact]
	public async Task FeatureDependencies_AddAndRemove_WorksAsExpected()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat1 = await _features.CreateAsync(board.Id, "Feat 1");
		var feat2 = await _features.CreateAsync(board.Id, "Feat 2");

		await _featureDeps.AddAsync(feat2.Id, feat1.Id);

		var deps = await _featureDeps.GetForFeatureAsync(feat2.Id);
		Assert.Single(deps);
		Assert.Equal(feat1.Id, deps[0].DependsOnFeatureId);

		// Duplicate add is idempotent
		await _featureDeps.AddAsync(feat2.Id, feat1.Id);
		Assert.Single(await _featureDeps.GetForFeatureAsync(feat2.Id));

		// Remove dependency
		await _featureDeps.RemoveAsync(feat2.Id, feat1.Id);
		Assert.Empty(await _featureDeps.GetForFeatureAsync(feat2.Id));
	}

	[Fact]
	public async Task FeatureDependencies_RejectsSelfDependency()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");

		await Assert.ThrowsAsync<InvalidOperationException>(() => _featureDeps.AddAsync(feat.Id, feat.Id));
	}

	[Fact]
	public async Task FeatureDependencies_RejectsCrossBoardDependency()
	{
		var board1 = await _boards.CreateAsync("Board 1", "/repos/b1");
		var board2 = await _boards.CreateAsync("Board 2", "/repos/b2");
		var feat1 = await _features.CreateAsync(board1.Id, "Feat 1");
		var feat2 = await _features.CreateAsync(board2.Id, "Feat 2");

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _featureDeps.AddAsync(feat1.Id, feat2.Id));
		Assert.Equal("Feature dependencies must remain within the same board.", ex.Message);
	}

	[Fact]
	public async Task FeatureDependencies_RejectsCircularDependency()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat1 = await _features.CreateAsync(board.Id, "Feat 1");
		var feat2 = await _features.CreateAsync(board.Id, "Feat 2");
		var feat3 = await _features.CreateAsync(board.Id, "Feat 3");

		await _featureDeps.AddAsync(feat2.Id, feat1.Id);
		await _featureDeps.AddAsync(feat3.Id, feat2.Id);

		// Direct cycle: feat1 -> feat2 (since feat2 -> feat1)
		await Assert.ThrowsAsync<InvalidOperationException>(() => _featureDeps.AddAsync(feat1.Id, feat2.Id));

		// Indirect cycle: feat1 -> feat3 (feat3 -> feat2 -> feat1)
		await Assert.ThrowsAsync<InvalidOperationException>(() => _featureDeps.AddAsync(feat1.Id, feat3.Id));
	}

	[Fact]
	public async Task FeatureDependencies_RejectsEditingWhenNotInBacklog()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat1 = await _features.CreateAsync(board.Id, "Feat 1");
		var feat2 = await _features.CreateAsync(board.Id, "Feat 2");

		feat1.WorkflowColumn = WorkflowColumn.Ready;
		await _dbContext.SaveChangesAsync();

		await Assert.ThrowsAsync<InvalidOperationException>(() => _featureDeps.AddAsync(feat1.Id, feat2.Id));
		await Assert.ThrowsAsync<InvalidOperationException>(() => _featureDeps.RemoveAsync(feat1.Id, feat2.Id));
	}

	[Fact]
	public async Task FeatureDependencies_AreDependenciesMetAsync_ChecksDoneState()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat1 = await _features.CreateAsync(board.Id, "Feat 1");
		var feat2 = await _features.CreateAsync(board.Id, "Feat 2");
		var feat3 = await _features.CreateAsync(board.Id, "Feat 3");

		// When no dependencies, returns true
		Assert.True(await _featureDeps.AreDependenciesMetAsync(feat3.Id));

		await _featureDeps.AddAsync(feat3.Id, feat1.Id);
		await _featureDeps.AddAsync(feat3.Id, feat2.Id);

		// Both are Backlog -> not met
		Assert.False(await _featureDeps.AreDependenciesMetAsync(feat3.Id));
		var unmet = await _featureDeps.GetUnmetDependenciesAsync(feat3.Id);
		Assert.Equal(2, unmet.Count);

		// Complete feat1
		feat1.WorkflowColumn = WorkflowColumn.Done;
		await _dbContext.SaveChangesAsync();

		// feat2 still not Done -> not met
		Assert.False(await _featureDeps.AreDependenciesMetAsync(feat3.Id));
		unmet = await _featureDeps.GetUnmetDependenciesAsync(feat3.Id);
		Assert.Single(unmet);
		Assert.Equal(feat2.Id, unmet[0].Id);

		// Complete feat2
		feat2.WorkflowColumn = WorkflowColumn.Done;
		await _dbContext.SaveChangesAsync();

		// Now all are Done -> met!
		Assert.True(await _featureDeps.AreDependenciesMetAsync(feat3.Id));
		Assert.Empty(await _featureDeps.GetUnmetDependenciesAsync(feat3.Id));
	}

	[Fact]
	public async Task StepDependencies_AddAndRemove_WorksAsExpected()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step1 = await _steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await _steps.CreateAsync(feat.Id, "Step 2");

		await _stepDeps.AddAsync(step2.Id, step1.Id);

		var deps = await _stepDeps.GetForStepAsync(step2.Id);
		Assert.Single(deps);
		Assert.Equal(step1.Id, deps[0].DependsOnStepId);

		// Duplicate add is idempotent
		await _stepDeps.AddAsync(step2.Id, step1.Id);
		Assert.Single(await _stepDeps.GetForStepAsync(step2.Id));

		// Remove dependency
		await _stepDeps.RemoveAsync(step2.Id, step1.Id);
		Assert.Empty(await _stepDeps.GetForStepAsync(step2.Id));
	}

	[Fact]
	public async Task StepDependencies_RejectsSelfDependency()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		await Assert.ThrowsAsync<InvalidOperationException>(() => _stepDeps.AddAsync(step.Id, step.Id));
	}

	[Fact]
	public async Task StepDependencies_RejectsCrossFeatureDependency()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat1 = await _features.CreateAsync(board.Id, "Feat 1");
		var feat2 = await _features.CreateAsync(board.Id, "Feat 2");
		var step1 = await _steps.CreateAsync(feat1.Id, "Step 1");
		var step2 = await _steps.CreateAsync(feat2.Id, "Step 2");

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _stepDeps.AddAsync(step1.Id, step2.Id));
		Assert.Equal("Step dependencies must remain within the same feature.", ex.Message);
	}

	[Fact]
	public async Task StepDependencies_RejectsCircularDependency()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step1 = await _steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await _steps.CreateAsync(feat.Id, "Step 2");
		var step3 = await _steps.CreateAsync(feat.Id, "Step 3");

		await _stepDeps.AddAsync(step2.Id, step1.Id);
		await _stepDeps.AddAsync(step3.Id, step2.Id);

		// Direct cycle: step1 -> step2 (since step2 -> step1)
		await Assert.ThrowsAsync<InvalidOperationException>(() => _stepDeps.AddAsync(step1.Id, step2.Id));

		// Indirect cycle: step1 -> step3 (step3 -> step2 -> step1)
		await Assert.ThrowsAsync<InvalidOperationException>(() => _stepDeps.AddAsync(step1.Id, step3.Id));
	}

	[Fact]
	public async Task StepDependencies_AllowsEditingInReadyBeforeStart_ButRejectsAfterStart()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step1 = await _steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await _steps.CreateAsync(feat.Id, "Step 2");

		// In Ready before start (no branch): editing allowed
		step1.WorkflowColumn = WorkflowColumn.Ready;
		step2.WorkflowColumn = WorkflowColumn.Ready;
		await _dbContext.SaveChangesAsync();

		await _stepDeps.AddAsync(step2.Id, step1.Id);
		Assert.Single(await _stepDeps.GetForStepAsync(step2.Id));

		// Enter Build: editing locked
		step2.WorkflowColumn = WorkflowColumn.Build;
		step2.BranchName = "feature/step2";
		await _dbContext.SaveChangesAsync();

		await Assert.ThrowsAsync<InvalidOperationException>(() => _stepDeps.RemoveAsync(step2.Id, step1.Id));

		// Return to Ready while already started: still locked
		step2.WorkflowColumn = WorkflowColumn.Ready;
		await _dbContext.SaveChangesAsync();
		await Assert.ThrowsAsync<InvalidOperationException>(() => _stepDeps.RemoveAsync(step2.Id, step1.Id));

		// Return to Backlog: unlocked!
		step2.WorkflowColumn = WorkflowColumn.Backlog;
		await _dbContext.SaveChangesAsync();
		await _stepDeps.RemoveAsync(step2.Id, step1.Id);
		Assert.Empty(await _stepDeps.GetForStepAsync(step2.Id));
	}

	[Fact]
	public async Task StepDependencies_AreDependenciesMetAsync_ChecksDoneState()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step1 = await _steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await _steps.CreateAsync(feat.Id, "Step 2");
		var step3 = await _steps.CreateAsync(feat.Id, "Step 3");

		Assert.True(await _stepDeps.AreDependenciesMetAsync(step3.Id));

		await _stepDeps.AddAsync(step3.Id, step1.Id);
		await _stepDeps.AddAsync(step3.Id, step2.Id);

		Assert.False(await _stepDeps.AreDependenciesMetAsync(step3.Id));

		step1.WorkflowColumn = WorkflowColumn.Done;
		await _dbContext.SaveChangesAsync();

		Assert.False(await _stepDeps.AreDependenciesMetAsync(step3.Id));

		step2.WorkflowColumn = WorkflowColumn.Done;
		await _dbContext.SaveChangesAsync();

		Assert.True(await _stepDeps.AreDependenciesMetAsync(step3.Id));
	}
}