namespace AgentTaskHarness.Domain.Entities;

public class AgentDefinitionComponent
{
	public Guid AgentDefinitionId { get; set; }
	public Guid ComponentId { get; set; }
	public int Order { get; set; }
	public AgentDefinition AgentDefinition { get; set; } = null!;
	public AgentComponent Component { get; set; } = null!;
}