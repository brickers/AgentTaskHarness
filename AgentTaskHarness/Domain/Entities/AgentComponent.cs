namespace AgentTaskHarness.Domain.Entities;

public class AgentComponent
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public string Name { get; set; } = string.Empty;
	public string ConfigContent { get; set; } = string.Empty;
	public ICollection<AgentDefinitionComponent> AgentDefinitions { get; set; } = new List<AgentDefinitionComponent>();
}