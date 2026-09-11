using System.Text.Json;
using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Infrastructure.Agents;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentTaskHarness.Tests;

public class AgentDefinitionCompositionTests : IAsyncLifetime
{
	private readonly SqliteConnection connection = new("Data Source=:memory:");
	private readonly string definitionFolder = Path.Combine(Path.GetTempPath(), $"agent-task-harness-{Guid.NewGuid():N}");
	private AppDbContext dbContext = null!;
	private BoardService boards = null!;
	private AgentDefinitionService definitions = null!;

	public async Task InitializeAsync()
	{
		await connection.OpenAsync();
		dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
		await dbContext.Database.MigrateAsync();
		boards = new BoardService(dbContext);
		definitions = new AgentDefinitionService(dbContext, new AgentDefinitionFolderWriter());
	}

	public async Task DisposeAsync()
	{
		await dbContext.DisposeAsync();
		await connection.DisposeAsync();
		if (Directory.Exists(definitionFolder))
		{
			Directory.Delete(definitionFolder, true);
		}
	}

	[Fact]
	public async Task ComponentOperationsAsync_AddRemoveAndReorderComponents()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var definition = await definitions.CreateAsync(board.Id, "Implementer", definitionFolder);
		var instructions = await definitions.CreateComponentAsync("Instructions", "Follow the task.");
		var review = await definitions.CreateComponentAsync("Review", "Run the tests.");

		await definitions.AddComponentAsync(definition.Id, instructions.Id);
		await definitions.AddComponentAsync(definition.Id, review.Id);
		await definitions.ReorderComponentsAsync(definition.Id, [review.Id, instructions.Id]);
		await definitions.RemoveComponentAsync(definition.Id, review.Id);

		var saved = (await definitions.GetByIdAsync(definition.Id))!;
		var component = Assert.Single(saved.Components);
		Assert.Equal(instructions.Id, component.ComponentId);
		Assert.Equal(0, component.Order);
	}

	[Fact]
	public async Task SaveAsync_WritesOrderedComponentConfigurationToDefinitionFolder()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var definition = await definitions.CreateAsync(board.Id, "Implementer", definitionFolder);
		var instructions = await definitions.CreateComponentAsync("Instructions", "Follow the task.");
		var review = await definitions.CreateComponentAsync("Review", "Run the tests.");

		await definitions.SaveAsync(definition.Id, "Reviewer", definitionFolder, [review.Id, instructions.Id]);

		await using var stream = File.OpenRead(Path.Combine(definitionFolder, "agent-definition.json"));
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