using System.Text.Json;
using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Domain.Entities;

namespace AgentTaskHarness.Infrastructure.Agents;

public class AgentDefinitionFolderWriter : IAgentDefinitionFolderWriter
{
	private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true
	};

	public async Task WriteAsync(AgentDefinition definition, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(definition);
		Directory.CreateDirectory(definition.FolderPath);

		var configuration = new AgentDefinitionConfiguration(
			definition.Name,
			definition.Components
				.OrderBy(component => component.Order)
				.Select(component =>
					new AgentComponentConfiguration(component.Component.Name, component.Component.ConfigContent))
				.ToList());
		var destinationPath = Path.Combine(definition.FolderPath, "agent-definition.json");
		var temporaryPath = Path.Combine(definition.FolderPath, $".agent-definition-{Guid.NewGuid():N}.tmp");

		try
		{
			await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(configuration, SerializerOptions),
				cancellationToken);
			File.Move(temporaryPath, destinationPath, true);
		}
		finally
		{
			if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
		}
	}

	private sealed record AgentDefinitionConfiguration(string Name, List<AgentComponentConfiguration> Components);

	private sealed record AgentComponentConfiguration(string Name, string ConfigContent);
}