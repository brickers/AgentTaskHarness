namespace AgentTaskHarness.Domain.Entities;

public class Column
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public Guid BoardId { get; set; }
	public string Name { get; set; } = string.Empty;
	public int Order { get; set; }
	public bool IsBacklog { get; set; }
	public bool IsTerminal { get; set; }
	public Guid? AgentDefinitionId { get; set; }
	public Board Board { get; set; } = null!;
	public AgentDefinition? AgentDefinition { get; set; }
	public ICollection<TaskItem> Tasks { get; set; } = new List<TaskItem>();
	public ICollection<ColumnTransition> OutgoingTransitions { get; set; } = new List<ColumnTransition>();
	public ICollection<ColumnTransition> IncomingTransitions { get; set; } = new List<ColumnTransition>();
	public ICollection<AgentRun> AgentRuns { get; set; } = new List<AgentRun>();
}