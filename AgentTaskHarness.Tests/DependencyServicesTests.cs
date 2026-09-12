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
	private readonly SqliteConnection connection = new("Data Source=:memory:");
	private AppDbContext dbContext = null!;
	private BoardService boards = null!;
	private FeatureService features = null!;
	private StepService steps = null!;
	private FeatureDependencyService featureDeps = null!;
	private StepDependencyService stepDeps = null!;

	public async Task InitializeAsync()
	{
		await connection.OpenAsync();
		dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
		await dbContext.Database.MigrateAsync();
		boards = new BoardService(dbContext);
		features = new FeatureService(dbContext);
		steps = new StepService(dbContext);
		featureDeps = new FeatureDependencyService(dbContext);
		stepDeps = new StepDependencyService(dbContext);
	}

	public async Task DisposeAsync()
	{
		await dbContext.DisposeAsync();
		await connection.DisposeAsync();
	}

	[Fact]
	public async Task FeatureDependencies_AddAndRemove_WorksAsExpected()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat1 = await features.CreateAsync(board.Id, "Feat 1");
		var feat2 = await features.CreateAsync(board.Id, "Feat 2");

		await featureDeps.AddAsync(feat2.Id, feat1.Id);

		var deps = await featureDeps.GetForFeatureAsync(feat2.Id);
		Assert.Single(deps);
		Assert.Equal(feat1.Id, deps[0].DependsOnFeatureId);

		// Duplicate add is idempotent
		await featureDeps.AddAsync(feat2.Id, feat1.Id);
		Assert.Single(await featureDeps.GetForFeatureAsync(feat2.Id));

		// Remove dependency
		await featureDeps.RemoveAsync(feat2.Id, feat1.Id);
		Assert.Empty(await featureDeps.GetForFeatureAsync(feat2.Id));
	}

	[Fact]
	public async Task FeatureDependencies_RejectsSelfDependency()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");

		await Assert.ThrowsAsync<InvalidOperationException>(() => featureDeps.AddAsync(feat.Id, feat.Id));
	}

	[Fact]
	public async Task FeatureDependencies_RejectsCrossBoardDependency()
	{
		var board1 = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var board2 = await boards.CreateAsync("Board 2", "/repos/b2", 1);
		var feat1 = await features.CreateAsync(board1.Id, "Feat 1");
		var feat2 = await features.CreateAsync(board2.Id, "Feat 2");

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => featureDeps.AddAsync(feat1.Id, feat2.Id));
		Assert.Equal("Feature dependencies must remain within the same board.", ex.Message);
	}

	[Fact]
	public async Task FeatureDependencies_RejectsCircularDependency()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat1 = await features.CreateAsync(board.Id, "Feat 1");
		var feat2 = await features.CreateAsync(board.Id, "Feat 2");
		var feat3 = await features.CreateAsync(board.Id, "Feat 3");

		await featureDeps.AddAsync(feat2.Id, feat1.Id);
		await featureDeps.AddAsync(feat3.Id, feat2.Id);

		// Direct cycle: feat1 -> feat2 (since feat2 -> feat1)
		await Assert.ThrowsAsync<InvalidOperationException>(() => featureDeps.AddAsync(feat1.Id, feat2.Id));

		// Indirect cycle: feat1 -> feat3 (feat3 -> feat2 -> feat1)
		await Assert.ThrowsAsync<InvalidOperationException>(() => featureDeps.AddAsync(feat1.Id, feat3.Id));
	}

	[Fact]
	public async Task FeatureDependencies_RejectsEditingWhenNotInBacklog()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat1 = await features.CreateAsync(board.Id, "Feat 1");
		var feat2 = await features.CreateAsync(board.Id, "Feat 2");

		feat1.WorkflowColumn = WorkflowColumn.Ready;
		await dbContext.SaveChangesAsync();

		await Assert.ThrowsAsync<InvalidOperationException>(() => featureDeps.AddAsync(feat1.Id, feat2.Id));
		await Assert.ThrowsAsync<InvalidOperationException>(() => featureDeps.RemoveAsync(feat1.Id, feat2.Id));
	}

	[Fact]
	public async Task FeatureDependencies_AreDependenciesMetAsync_ChecksDoneState()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat1 = await features.CreateAsync(board.Id, "Feat 1");
		var feat2 = await features.CreateAsync(board.Id, "Feat 2");
		var feat3 = await features.CreateAsync(board.Id, "Feat 3");

		// When no dependencies, returns true
		Assert.True(await featureDeps.AreDependenciesMetAsync(feat3.Id));

		await featureDeps.AddAsync(feat3.Id, feat1.Id);
		await featureDeps.AddAsync(feat3.Id, feat2.Id);

		// Both are Backlog -> not met
		Assert.False(await featureDeps.AreDependenciesMetAsync(feat3.Id));
		var unmet = await featureDeps.GetUnmetDependenciesAsync(feat3.Id);
		Assert.Equal(2, unmet.Count);

		// Complete feat1
		feat1.WorkflowColumn = WorkflowColumn.Done;
		await dbContext.SaveChangesAsync();

		// feat2 still not Done -> not met
		Assert.False(await featureDeps.AreDependenciesMetAsync(feat3.Id));
		unmet = await featureDeps.GetUnmetDependenciesAsync(feat3.Id);
		Assert.Single(unmet);
		Assert.Equal(feat2.Id, unmet[0].Id);

		// Complete feat2
		feat2.WorkflowColumn = WorkflowColumn.Done;
		await dbContext.SaveChangesAsync();

		// Now all are Done -> met!
		Assert.True(await featureDeps.AreDependenciesMetAsync(feat3.Id));
		Assert.Empty(await featureDeps.GetUnmetDependenciesAsync(feat3.Id));
	}

	[Fact]
	public async Task StepDependencies_AddAndRemove_WorksAsExpected()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step1 = await steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await steps.CreateAsync(feat.Id, "Step 2");

		await stepDeps.AddAsync(step2.Id, step1.Id);

		var deps = await stepDeps.GetForStepAsync(step2.Id);
		Assert.Single(deps);
		Assert.Equal(step1.Id, deps[0].DependsOnStepId);

		// Duplicate add is idempotent
		await stepDeps.AddAsync(step2.Id, step1.Id);
		Assert.Single(await stepDeps.GetForStepAsync(step2.Id));

		// Remove dependency
		await stepDeps.RemoveAsync(step2.Id, step1.Id);
		Assert.Empty(await stepDeps.GetForStepAsync(step2.Id));
	}

	[Fact]
	public async Task StepDependencies_RejectsSelfDependency()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await Assert.ThrowsAsync<InvalidOperationException>(() => stepDeps.AddAsync(step.Id, step.Id));
	}

	[Fact]
	public async Task StepDependencies_RejectsCrossFeatureDependency()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat1 = await features.CreateAsync(board.Id, "Feat 1");
		var feat2 = await features.CreateAsync(board.Id, "Feat 2");
		var step1 = await steps.CreateAsync(feat1.Id, "Step 1");
		var step2 = await steps.CreateAsync(feat2.Id, "Step 2");

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => stepDeps.AddAsync(step1.Id, step2.Id));
		Assert.Equal("Step dependencies must remain within the same feature.", ex.Message);
	}

	[Fact]
	public async Task StepDependencies_RejectsCircularDependency()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step1 = await steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await steps.CreateAsync(feat.Id, "Step 2");
		var step3 = await steps.CreateAsync(feat.Id, "Step 3");

		await stepDeps.AddAsync(step2.Id, step1.Id);
		await stepDeps.AddAsync(step3.Id, step2.Id);

		// Direct cycle: step1 -> step2 (since step2 -> step1)
		await Assert.ThrowsAsync<InvalidOperationException>(() => stepDeps.AddAsync(step1.Id, step2.Id));

		// Indirect cycle: step1 -> step3 (step3 -> step2 -> step1)
		await Assert.ThrowsAsync<InvalidOperationException>(() => stepDeps.AddAsync(step1.Id, step3.Id));
	}

	[Fact]
	public async Task StepDependencies_AllowsEditingInReadyBeforeStart_ButRejectsAfterStart()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step1 = await steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await steps.CreateAsync(feat.Id, "Step 2");

		// In Ready before start (no branch): editing allowed
		step1.WorkflowColumn = WorkflowColumn.Ready;
		step2.WorkflowColumn = WorkflowColumn.Ready;
		await dbContext.SaveChangesAsync();

		await stepDeps.AddAsync(step2.Id, step1.Id);
		Assert.Single(await stepDeps.GetForStepAsync(step2.Id));

		// Enter Build: editing locked
		step2.WorkflowColumn = WorkflowColumn.Build;
		step2.BranchName = "feature/step2";
		await dbContext.SaveChangesAsync();

		await Assert.ThrowsAsync<InvalidOperationException>(() => stepDeps.RemoveAsync(step2.Id, step1.Id));

		// Return to Ready while already started: still locked
		step2.WorkflowColumn = WorkflowColumn.Ready;
		await dbContext.SaveChangesAsync();
		await Assert.ThrowsAsync<InvalidOperationException>(() => stepDeps.RemoveAsync(step2.Id, step1.Id));

		// Return to Backlog: unlocked!
		step2.WorkflowColumn = WorkflowColumn.Backlog;
		await dbContext.SaveChangesAsync();
		await stepDeps.RemoveAsync(step2.Id, step1.Id);
		Assert.Empty(await stepDeps.GetForStepAsync(step2.Id));
	}

	[Fact]
	public async Task StepDependencies_AreDependenciesMetAsync_ChecksDoneState()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step1 = await steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await steps.CreateAsync(feat.Id, "Step 2");
		var step3 = await steps.CreateAsync(feat.Id, "Step 3");

		Assert.True(await stepDeps.AreDependenciesMetAsync(step3.Id));

		await stepDeps.AddAsync(step3.Id, step1.Id);
		await stepDeps.AddAsync(step3.Id, step2.Id);

		Assert.False(await stepDeps.AreDependenciesMetAsync(step3.Id));

		step1.WorkflowColumn = WorkflowColumn.Done;
		await dbContext.SaveChangesAsync();

		Assert.False(await stepDeps.AreDependenciesMetAsync(step3.Id));

		step2.WorkflowColumn = WorkflowColumn.Done;
		await dbContext.SaveChangesAsync();

		Assert.True(await stepDeps.AreDependenciesMetAsync(step3.Id));
	}
}
