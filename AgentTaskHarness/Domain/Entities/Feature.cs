using AgentTaskHarness.Domain.Enums;

namespace AgentTaskHarness.Domain.Entities;

public class Feature
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public Guid BoardId { get; set; }
	public string Title { get; set; } = string.Empty;
	public string Requirements { get; set; } = string.Empty;
	public string AcceptanceCriteria { get; set; } = string.Empty;
	public string SuggestedSolution { get; set; } = string.Empty;
	public WorkflowColumn WorkflowColumn { get; set; } = WorkflowColumn.Backlog;
	public bool AlwaysRequireHumanReview { get; set; }
	public int AgentReviewFailCount { get; set; }
	public int HumanReviewFailCount { get; set; }
	public string? BranchName { get; set; }
	public string? WorktreePath { get; set; }
	public bool MergeConflictPending { get; set; }
	public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

	public Board Board { get; set; } = null!;
	public ICollection<Step> Steps { get; set; } = new List<Step>();
	public ICollection<FeatureDependency> Dependencies { get; set; } = new List<FeatureDependency>();
	public ICollection<FeatureDependency> DependedOnBy { get; set; } = new List<FeatureDependency>();
}
