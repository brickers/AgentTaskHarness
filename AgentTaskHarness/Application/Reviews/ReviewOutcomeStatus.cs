namespace AgentTaskHarness.Application.Reviews;

public record ReviewOutcomeStatus(
	bool IsRework,
	bool HasIssue,
	int AgentReviewFailCount,
	int HumanReviewFailCount,
	int AgentReviewFailThreshold,
	int HumanReviewFailThreshold,
	bool AgentReviewThresholdCrossed,
	bool HumanReviewThresholdCrossed);