namespace AgentTaskHarness.Domain.Entities;

public class Board
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public string Name { get; set; } = string.Empty;
	public string RepoPath { get; set; } = string.Empty;
	public int ConcurrencyLimit { get; set; } = 1;
	public ICollection<Column> Columns { get; set; } = new List<Column>();
	public ICollection<TaskItem> Tasks { get; set; } = new List<TaskItem>();
	public ICollection<AgentDefinition> AgentDefinitions { get; set; } = new List<AgentDefinition>();
}