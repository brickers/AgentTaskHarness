using AgentTaskHarness.Domain.Enums;

namespace AgentTaskHarness.Domain.Entities;

public class AgentColumnAssignment
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public Guid BoardId { get; set; }
	public ColumnScope ColumnScope { get; set; }
	public Guid AgentDefinitionId { get; set; }
	public string MatchCriteria { get; set; } = string.Empty;

	public Board Board { get; set; } = null!;
	public AgentDefinition AgentDefinition { get; set; } = null!;
}
