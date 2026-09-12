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

public class AgentMatchingServiceTests : IAsyncLifetime
{
	private readonly SqliteConnection _connection = new("Data Source=:memory:");
	private BoardService _boards = null!;
	private AppDbContext _dbContext = null!;
	private AgentDefinitionService _definitions = null!;
	private FeatureService _features = null!;
	private AgentMatchingService _matching = null!;
	private StepService _steps = null!;

	public async Task InitializeAsync()
	{
		await _connection.OpenAsync();
		_dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
		await _dbContext.Database.MigrateAsync();

		_boards = new BoardService(_dbContext);
		_features = new FeatureService(_dbContext);
		_steps = new StepService(_dbContext);
		_definitions = new AgentDefinitionService(_dbContext, new AgentDefinitionFolderWriter());
		_matching = new AgentMatchingService(_dbContext);
	}

	public async Task DisposeAsync()
	{
		await _dbContext.DisposeAsync();
		await _connection.DisposeAsync();
	}

	[Fact]
	public async Task AssignmentCrud_CreateGetUpdateDeleteAsync()
	{
		var board = await _boards.CreateAsync("Test Board", "/repos/test");
		var agent1 = await _definitions.CreateAsync(board.Id, "DevAgent", "/defs/dev");
		var agent2 = await _definitions.CreateAsync(board.Id, "SeniorDevAgent", "/defs/sr-dev");

		// Assign
		var created = await _matching.AssignAgentAsync(board.Id, ColumnScope.StepBuild, agent1.Id, "rework=false");
		Assert.NotNull(created);
		Assert.Equal(board.Id, created.BoardId);
		Assert.Equal(ColumnScope.StepBuild, created.ColumnScope);
		Assert.Equal(agent1.Id, created.AgentDefinitionId);
		Assert.Equal("rework=false", created.MatchCriteria);

		// Get
		var all = await _matching.GetAssignmentsAsync(board.Id);
		var single = Assert.Single(all);
		Assert.Equal(created.Id, single.Id);
		Assert.Equal("DevAgent", single.AgentDefinition.Name);

		var scoped = await _matching.GetAssignmentsForScopeAsync(board.Id, ColumnScope.StepBuild);
		Assert.Single(scoped);
		var emptyScope = await _matching.GetAssignmentsForScopeAsync(board.Id, ColumnScope.StepAgentReview);
		Assert.Empty(emptyScope);

		// Update both agent and criteria
		var updated = await _matching.UpdateAssignmentAsync(created.Id, agent2.Id, "rework=true");
		Assert.Equal("rework=true", updated.MatchCriteria);
		Assert.Equal(agent2.Id, updated.AgentDefinitionId);
		Assert.Equal("SeniorDevAgent", updated.AgentDefinition.Name);

		// Delete
		await _matching.DeleteAssignmentAsync(created.Id);
		var afterDelete = await _matching.GetAssignmentsAsync(board.Id);
		Assert.Empty(afterDelete);
	}

	[Fact]
	public async Task AssignmentCrud_ValidationsAsync()
	{
		var board = await _boards.CreateAsync("Val Board", "/repos/val");
		var agent = await _definitions.CreateAsync(board.Id, "ValAgent", "/defs/val");

		// Invalid ColumnScope
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
			_matching.AssignAgentAsync(board.Id, (ColumnScope)999, agent.Id));

		// Non-existent Board
		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			_matching.AssignAgentAsync(Guid.NewGuid(), ColumnScope.StepBuild, agent.Id));

		// Non-existent AgentDefinition
		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			_matching.AssignAgentAsync(board.Id, ColumnScope.StepBuild, Guid.NewGuid()));

		// Non-existent assignment for Update / Delete
		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			_matching.UpdateAssignmentAsync(Guid.NewGuid(), agent.Id));
		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			_matching.DeleteAssignmentAsync(Guid.NewGuid()));
	}

	[Fact]
	public async Task CascadeDelete_WhenBoardOrAgentDeletedAsync()
	{
		var board = await _boards.CreateAsync("Cascade Board", "/repos/cascade");
		var agent1 = await _definitions.CreateAsync(board.Id, "Agent 1", "/defs/1");
		var agent2 = await _definitions.CreateAsync(board.Id, "Agent 2", "/defs/2");

		await _matching.AssignAgentAsync(board.Id, ColumnScope.StepBuild, agent1.Id, "");
		var assignment2 = await _matching.AssignAgentAsync(board.Id, ColumnScope.StepAgentReview, agent2.Id, "");

		// Deleting agent1 cascades delete to assignment1
		await _definitions.DeleteAsync(agent1.Id);
		var remaining = await _matching.GetAssignmentsAsync(board.Id);
		var left = Assert.Single(remaining);
		Assert.Equal(assignment2.Id, left.Id);

		// Deleting board cascades delete to assignment2
		await _boards.DeleteAsync(board.Id);
		var none = await _dbContext.AgentColumnAssignments.Where(a => a.BoardId == board.Id).ToListAsync();
		Assert.Empty(none);
	}

	[Fact]
	public async Task ResolveAgent_ReworkMatchingCriteriaAsync()
	{
		var board = await _boards.CreateAsync("Matching Board", "/repos/match");
		var newDevAgent = await _definitions.CreateAsync(board.Id, "NewDevAgent", "/defs/new");
		var fixBugAgent = await _definitions.CreateAsync(board.Id, "FixBugAgent", "/defs/fix");
		var fallbackAgent = await _definitions.CreateAsync(board.Id, "FallbackAgent", "/defs/fallback");

		await _matching.AssignAgentAsync(board.Id, ColumnScope.StepBuild, fallbackAgent.Id, "");
		await _matching.AssignAgentAsync(board.Id, ColumnScope.StepBuild, newDevAgent.Id, "rework=false");
		await _matching.AssignAgentAsync(board.Id, ColumnScope.StepBuild, fixBugAgent.Id, "rework=true");

		var feature = await _features.CreateAsync(board.Id, "Feature 1", "Reqs", "Criteria", "Solution");
		var step = await _steps.CreateAsync(feature.Id, "Step 1", "Desc", "Notes");

		// Brand new step (fails = 0) -> should match newDevAgent
		var resolvedNew = await _matching.ResolveAgentAsync(step, ColumnScope.StepBuild);
		Assert.NotNull(resolvedNew);
		Assert.Equal(newDevAgent.Id, resolvedNew.Id);

		// Simulate review failure
		step.AgentReviewFailCount = 1;
		await _dbContext.SaveChangesAsync();

		// Rework step (AgentReviewFailCount > 0) -> should match fixBugAgent
		var resolvedFix = await _matching.ResolveAgentAsync(step, ColumnScope.StepBuild);
		Assert.NotNull(resolvedFix);
		Assert.Equal(fixBugAgent.Id, resolvedFix.Id);
	}

	[Fact]
	public async Task ResolveAgent_SpecificityRankingAsync()
	{
		var board = await _boards.CreateAsync("Specificity Board", "/repos/spec");
		var defaultAgent = await _definitions.CreateAsync(board.Id, "DefaultAgent", "/defs/def");
		var reworkAgent = await _definitions.CreateAsync(board.Id, "ReworkAgent", "/defs/rework");
		var authReworkAgent = await _definitions.CreateAsync(board.Id, "AuthReworkAgent", "/defs/auth-rework");

		await _matching.AssignAgentAsync(board.Id, ColumnScope.StepBuild, defaultAgent.Id, "");
		await _matching.AssignAgentAsync(board.Id, ColumnScope.StepBuild, reworkAgent.Id, "rework=true");
		await _matching.AssignAgentAsync(board.Id, ColumnScope.StepBuild, authReworkAgent.Id,
			"rework=true, title:auth");

		var feature = await _features.CreateAsync(board.Id, "Feature", "Reqs", "Criteria", "Solution");
		var normalStep = await _steps.CreateAsync(feature.Id, "Implement cache", "Desc", "Notes");
		normalStep.AgentReviewFailCount = 1;
		await _dbContext.SaveChangesAsync();

		// Rework but not auth -> ReworkAgent
		var resolved1 = await _matching.ResolveAgentAsync(normalStep, ColumnScope.StepBuild);
		Assert.NotNull(resolved1);
		Assert.Equal(reworkAgent.Id, resolved1.Id);

		// Rework AND auth -> AuthReworkAgent (more specific)
		var authStep = await _steps.CreateAsync(feature.Id, "Implement auth token refresh", "Desc", "Notes");
		authStep.HumanReviewFailCount = 1;
		await _dbContext.SaveChangesAsync();

		var resolved2 = await _matching.ResolveAgentAsync(authStep, ColumnScope.StepBuild);
		Assert.NotNull(resolved2);
		Assert.Equal(authReworkAgent.Id, resolved2.Id);
	}

	[Fact]
	public async Task ResolveAgent_JsonCriteriaAsync()
	{
		var board = await _boards.CreateAsync("JSON Board", "/repos/json");
		var jsonAgent = await _definitions.CreateAsync(board.Id, "JsonAgent", "/defs/json");

		await _matching.AssignAgentAsync(board.Id, ColumnScope.StepBuild, jsonAgent.Id,
			"{\"rework\": true, \"keyword\": \"payment\"}");

		var feature = await _features.CreateAsync(board.Id, "Billing Feature", "Payment requirements", "Criteria",
			"Solution");
		var step = await _steps.CreateAsync(feature.Id, "Payment gateway integration", "Payment details", "Notes");

		// Fails = 0, so rework=true doesn't match
		var noMatch = await _matching.ResolveAgentAsync(step, ColumnScope.StepBuild);
		Assert.Null(noMatch);

		// Now make it rework
		step.AgentReviewFailCount = 1;
		await _dbContext.SaveChangesAsync();

		var match = await _matching.ResolveAgentAsync(step, ColumnScope.StepBuild);
		Assert.NotNull(match);
		Assert.Equal(jsonAgent.Id, match.Id);
	}

	[Fact]
	public async Task ResolveAgent_FeatureAgentReviewAsync()
	{
		var board = await _boards.CreateAsync("Feature Board", "/repos/feature");
		var featureReviewAgent = await _definitions.CreateAsync(board.Id, "FeatureReviewer", "/defs/frev");

		await _matching.AssignAgentAsync(board.Id, ColumnScope.FeatureAgentReview, featureReviewAgent.Id, "");

		var feature = await _features.CreateAsync(board.Id, "Feature 1", "Reqs", "Criteria", "Solution");

		var resolved = await _matching.ResolveAgentAsync(feature, ColumnScope.FeatureAgentReview);
		Assert.NotNull(resolved);
		Assert.Equal(featureReviewAgent.Id, resolved.Id);

		// Feature using CardType overload
		var resolvedViaCardType =
			await _matching.ResolveAgentAsync(CardType.Feature, feature.Id, ColumnScope.FeatureAgentReview);
		Assert.NotNull(resolvedViaCardType);
		Assert.Equal(featureReviewAgent.Id, resolvedViaCardType.Id);
	}

	[Fact]
	public async Task ScopeGating_DisallowsMismatchedScopes()
	{
		var board = await _boards.CreateAsync("Gating Board", "/repos/gate");
		var feature = await _features.CreateAsync(board.Id, "Feature 1", "Reqs", "Criteria", "Solution");
		var step = await _steps.CreateAsync(feature.Id, "Step 1", "Desc", "Notes");

		// Step cannot be resolved for FeatureAgentReview
		await Assert.ThrowsAsync<ArgumentException>(() =>
			_matching.ResolveAgentAsync(step, ColumnScope.FeatureAgentReview));

		// Feature cannot be resolved for Step scopes
		await Assert.ThrowsAsync<ArgumentException>(() => _matching.ResolveAgentAsync(feature, ColumnScope.StepBuild));
		await Assert.ThrowsAsync<ArgumentException>(() =>
			_matching.ResolveAgentAsync(feature, ColumnScope.StepAgentReview));

		// Invalid CardType
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
			_matching.ResolveAgentAsync((CardType)99, Guid.NewGuid(), ColumnScope.StepBuild));
	}

	[Fact]
	public async Task ScopeIsolation_DoesNotCrossColumnScopes()
	{
		var board = await _boards.CreateAsync("Iso Board", "/repos/iso");
		var buildAgent = await _definitions.CreateAsync(board.Id, "Builder", "/defs/build");
		var reviewAgent = await _definitions.CreateAsync(board.Id, "Reviewer", "/defs/rev");

		await _matching.AssignAgentAsync(board.Id, ColumnScope.StepBuild, buildAgent.Id, "");
		await _matching.AssignAgentAsync(board.Id, ColumnScope.StepAgentReview, reviewAgent.Id, "");

		var feature = await _features.CreateAsync(board.Id, "Feature 1", "Reqs", "Criteria", "Solution");
		var step = await _steps.CreateAsync(feature.Id, "Step 1", "Desc", "Notes");

		var resolvedBuild = await _matching.ResolveAgentAsync(step, ColumnScope.StepBuild);
		Assert.Equal(buildAgent.Id, resolvedBuild!.Id);

		var resolvedReview = await _matching.ResolveAgentAsync(step, ColumnScope.StepAgentReview);
		Assert.Equal(reviewAgent.Id, resolvedReview!.Id);
	}

	[Fact]
	public void EvaluateCriteria_HandlesWildcardAndDefaultValues()
	{
		var step = new Step { Title = "Task A", Description = "Some description" };
		var feature = new Feature { Title = "Feature A", Requirements = "Reqs" };

		Assert.True(AgentMatchingService.EvaluateCriteria(null, step, out var spec1));
		Assert.Equal(0, spec1);

		Assert.True(AgentMatchingService.EvaluateCriteria("", step, out var spec2));
		Assert.Equal(0, spec2);

		Assert.True(AgentMatchingService.EvaluateCriteria("*", step, out var spec3));
		Assert.Equal(0, spec3);

		Assert.True(AgentMatchingService.EvaluateCriteria("default", step, out var spec4));
		Assert.Equal(0, spec4);

		Assert.True(AgentMatchingService.EvaluateCriteria("default", feature, out var spec5));
		Assert.Equal(0, spec5);
	}
}