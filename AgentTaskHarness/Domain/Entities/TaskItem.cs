using AgentTaskHarness.Domain.Enums;
using DomainTaskStatus = AgentTaskHarness.Domain.Enums.TaskStatus;

namespace AgentTaskHarness.Domain.Entities;

public class TaskItem
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public Guid BoardId { get; set; }
	public Guid ColumnId { get; set; }
	public string Title { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public DomainTaskStatus Status { get; set; } = DomainTaskStatus.Backlog;
	public string? BranchName { get; set; }
	public string? WorktreePath { get; set; }
	public bool MergeConflictPending { get; set; }
	public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
	public Board Board { get; set; } = null!;
	public Column Column { get; set; } = null!;
	public ICollection<TaskDependency> Dependencies { get; set; } = new List<TaskDependency>();
	public ICollection<TaskDependency> DependedOnBy { get; set; } = new List<TaskDependency>();
	public ICollection<AgentRun> AgentRuns { get; set; } = new List<AgentRun>();
}