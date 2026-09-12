namespace AgentTaskHarness.Domain.Entities;

public class AgentDefinition
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public Guid BoardId { get; set; }
	public string Name { get; set; } = string.Empty;
	public string FolderPath { get; set; } = string.Empty;
	public Board Board { get; set; } = null!;
	public ICollection<AgentDefinitionComponent> Components { get; set; } = new List<AgentDefinitionComponent>();
}
