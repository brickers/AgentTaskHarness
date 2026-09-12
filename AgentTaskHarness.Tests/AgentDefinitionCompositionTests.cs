using System.Text.Json;
using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Infrastructure.Agents;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentTaskHarness.Tests;

public class AgentDefinitionCompositionTests : IAsyncLifetime
{
	private readonly SqliteConnection _connection = new("Data Source=:memory:");

	private readonly string _definitionFolder =
		Path.Combine(Path.GetTempPath(), $"agent-task-harness-{Guid.NewGuid():N}");

	private BoardService _boards = null!;
	private AppDbContext _dbContext = null!;
	private AgentDefinitionService _definitions = null!;

	public async Task InitializeAsync()
	{
		await _connection.OpenAsync();
		_dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
		await _dbContext.Database.MigrateAsync();
		_boards = new BoardService(_dbContext);
		_definitions = new AgentDefinitionService(_dbContext, new AgentDefinitionFolderWriter());
	}

	public async Task DisposeAsync()
	{
		await _dbContext.DisposeAsync();
		await _connection.DisposeAsync();
		if (Directory.Exists(_definitionFolder)) Directory.Delete(_definitionFolder, true);
	}

	[Fact]
	public async Task ComponentOperationsAsync_AddRemoveAndReorderComponents()
	{
		var board = await _boards.CreateAsync("Harness", "/repos/harness");
		var definition = await _definitions.CreateAsync(board.Id, "Implementer", _definitionFolder);
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
	public async Task SaveAsync_WritesOrderedComponentConfigurationToDefinitionFolder()
	{
		var board = await _boards.CreateAsync("Harness", "/repos/harness");
		var definition = await _definitions.CreateAsync(board.Id, "Implementer", _definitionFolder);
		var instructions = await _definitions.CreateComponentAsync("Instructions", "Follow the task.");
		var review = await _definitions.CreateComponentAsync("Review", "Run the tests.");

		await _definitions.SaveAsync(definition.Id, "Reviewer", _definitionFolder, [review.Id, instructions.Id]);

		await using var stream = File.OpenRead(Path.Combine(_definitionFolder, "agent-definition.json"));
		using var document = await JsonDocument.ParseAsync(stream);
		Assert.Equal("Reviewer", document.RootElement.GetProperty("name").GetString());
		var components = document.RootElement.GetProperty("components").EnumerateArray().ToList();
		Assert.Collection(components,
			component =>
			{
				Assert.Equal("Review", component.GetProperty("name").GetString());
				Assert.Equal("Run the tests.", component.GetProperty("configContent").GetString());
			},
			component => Assert.Equal("Instructions", component.GetProperty("name").GetString()));
	}
}