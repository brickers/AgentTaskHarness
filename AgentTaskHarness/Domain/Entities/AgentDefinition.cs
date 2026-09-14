using System.ComponentModel.DataAnnotations.Schema;

namespace AgentTaskHarness.Domain.Entities;

public class AgentDefinition
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public string Name { get; set; } = string.Empty;
	public string Prompt { get; set; } = string.Empty;
	public string Instructions { get; set; } = string.Empty;
	public string ToolConfiguration { get; set; } = string.Empty;

	[NotMapped]
	[Obsolete("Agent definitions are stored in the database; use Prompt, Instructions, and ToolConfiguration.")]
	public string FolderPath { get; set; } = string.Empty;

	public ICollection<AgentDefinitionComponent> Components { get; set; } = new List<AgentDefinitionComponent>();
	public ICollection<AgentColumnAssignment> ColumnAssignments { get; set; } = new List<AgentColumnAssignment>();
}