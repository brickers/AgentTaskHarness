using AgentTaskHarness.Domain.Enums;

namespace AgentTaskHarness.Domain.Entities;

public class AgentRun
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public Guid TaskId { get; set; }
	public Guid ColumnId { get; set; }
	public int? ProcessId { get; set; }
	public AgentRunStatus Status { get; set; }
	public string? SessionLink { get; set; }
	public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
	public DateTimeOffset? EndedAt { get; set; }
	public TaskItem Task { get; set; } = null!;
	public Column Column { get; set; } = null!;
}