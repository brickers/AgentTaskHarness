using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentTaskHarness.Tests;

public class AgentDefinitionCompositionTests : IAsyncLifetime
{
	private readonly SqliteConnection _connection = new("Data Source=:memory:");

	private AppDbContext _dbContext = null!;
	private AgentDefinitionService _definitions = null!;

	public async Task InitializeAsync()
	{
		await _connection.OpenAsync();
		_dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
		await _dbContext.Database.MigrateAsync();
		_definitions = new AgentDefinitionService(_dbContext);
	}

	public async Task DisposeAsync()
	{
		await _dbContext.DisposeAsync();
		await _connection.DisposeAsync();
	}

	[Fact]
	public async Task ComponentOperationsAsync_AddRemoveAndReorderComponents()
	{
		var definition = await _definitions.CreateAsync("Implementer");
		var instructions = await _definitions.CreateComponentAsync("Instructions", "Follow the task.");
		var review = await _definitions.CreateComponentAsync("Review", "Run the tests.");

		await _definitions.AddComponentAsync(definition.Id, instructions.Id);
		await _definitions.AddComponentAsync(definition.Id, review.Id);
		await _definitions.ReorderComponentsAsync(definition.Id, [review.Id, instructions.Id]);
		await _definitions.RemoveComponentAsync(definition.Id, review.Id);

		var saved = (await _definitions.GetByIdAsync(definition.Id))!;
		var component = Assert.Single(saved.Components);
		Assert.Equal(instructions.Id, component.ComponentId);
		Assert.Equal(0, component.Order);
	}

	[Fact]
	public async Task SaveAsync_PersistsOrderedComponentConfiguration()
	{
		var definition = await _definitions.CreateAsync("Implementer");
		var instructions = await _definitions.CreateComponentAsync("Instructions", "Follow the task.");
		var review = await _definitions.CreateComponentAsync("Review", "Run the tests.");

		await _definitions.SaveAsync(definition.Id, "Reviewer", "Prompt", "Instructions", "Tools", [review.Id, instructions.Id]);

		var saved = (await _definitions.GetByIdAsync(definition.Id))!;
		Assert.Equal("Reviewer", saved.Name);
		Assert.Collection(saved.Components.OrderBy(component => component.Order),
			component => Assert.Equal("Review", component.Component.Name),
			component => Assert.Equal("Instructions", component.Component.Name));
	}
}