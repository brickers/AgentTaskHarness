namespace AgentTaskHarness.Domain.Entities;

public class Board
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public string Name { get; set; } = string.Empty;
	public string RepoPath { get; set; } = string.Empty;
	public int ConcurrencyLimit { get; set; } = 1;
	public bool SkipFeatureHumanReview { get; set; }
	public bool SkipStepHumanReview { get; set; }
	public int AgentReviewFailThreshold { get; set; } = 3;
	public int HumanReviewFailThreshold { get; set; } = 3;

	public ICollection<Feature> Features { get; set; } = new List<Feature>();
	public ICollection<AgentColumnAssignment> ColumnAssignments { get; set; } = new List<AgentColumnAssignment>();
}