using AgentTaskHarness.Domain.Enums;

namespace AgentTaskHarness.Domain.Entities;

public class Step
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public Guid FeatureId { get; set; }
	public string Title { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public string GuidanceNotes { get; set; } = string.Empty;
	public WorkflowColumn WorkflowColumn { get; set; } = WorkflowColumn.Backlog;
	public bool AlwaysRequireHumanReview { get; set; }
	public int AgentReviewFailCount { get; set; }
	public int HumanReviewFailCount { get; set; }
	public string? BranchName { get; set; }
	public string? WorktreePath { get; set; }
	public long TokensUsed { get; set; }
	public TimeSpan TimeSpent { get; set; } = TimeSpan.Zero;
	public bool MergeConflictPending { get; set; }
	public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

	public Feature Feature { get; set; } = null!;
	public ICollection<StepDependency> Dependencies { get; set; } = new List<StepDependency>();
	public ICollection<StepDependency> DependedOnBy { get; set; } = new List<StepDependency>();
}