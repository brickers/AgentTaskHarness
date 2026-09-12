using AgentTaskHarness.Domain.Enums;

namespace AgentTaskHarness.Application.Workflow;

public class WorkflowTransitionRules
{
	public bool IsAllowedTransition(WorkflowColumn current, WorkflowColumn target)
	{
		if (current == target) return false;

		if (current == WorkflowColumn.Done) return false;

		// Forward moves are adjacent-only, one column at a time:
		// Backlog -> Ready -> Build -> AgentReview -> HumanReview -> Done.
		if ((int)target == (int)current + 1) return true;

		// From any column except Done, a card can move backward directly to Backlog or Ready.
		if (target is WorkflowColumn.Backlog or WorkflowColumn.Ready) return (int)target < (int)current;

		// From Agent Review or Human Review, a card can move backward directly to Build (e.g. to send it back for rework).
		if (current is WorkflowColumn.AgentReview or WorkflowColumn.HumanReview &&
		    target == WorkflowColumn.Build) return true;

		return false;
	}

	public IReadOnlyList<WorkflowColumn> GetAllowedTransitions(WorkflowColumn current)
	{
		var transitions = new List<WorkflowColumn>();
		foreach (var candidate in Enum.GetValues<WorkflowColumn>())
			if (IsAllowedTransition(current, candidate))
				transitions.Add(candidate);

		return transitions;
	}

	public bool IsTerminal(WorkflowColumn column)
	{
		return column == WorkflowColumn.Done;
	}
}