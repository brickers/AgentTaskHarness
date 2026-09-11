namespace AgentTaskHarness.Domain.Entities;

public class TaskDependency
{
	public Guid TaskId { get; set; }
	public Guid DependsOnTaskId { get; set; }
	public TaskItem Task { get; set; } = null!;
	public TaskItem DependsOnTask { get; set; } = null!;
}