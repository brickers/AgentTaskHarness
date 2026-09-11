namespace AgentTaskHarness.Domain.Entities;

public class ColumnTransition
{
	public Guid FromColumnId { get; set; }
	public Guid ToColumnId { get; set; }
	public Column FromColumn { get; set; } = null!;
	public Column ToColumn { get; set; } = null!;
}