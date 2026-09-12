using AgentTaskHarness.Application.Workflow;
using AgentTaskHarness.Domain.Enums;
using Xunit;

namespace AgentTaskHarness.Tests;

public class WorkflowTransitionRulesTests
{
	private readonly WorkflowTransitionRules rules = new();

	[Theory]
	[InlineData(WorkflowColumn.Backlog, WorkflowColumn.Ready)]
	[InlineData(WorkflowColumn.Ready, WorkflowColumn.Build)]
	[InlineData(WorkflowColumn.Build, WorkflowColumn.AgentReview)]
	[InlineData(WorkflowColumn.AgentReview, WorkflowColumn.HumanReview)]
	[InlineData(WorkflowColumn.HumanReview, WorkflowColumn.Done)]
	public void IsAllowedTransition_AllowsValidForwardAdjacentMoves(WorkflowColumn from, WorkflowColumn to)
	{
		Assert.True(rules.IsAllowedTransition(from, to));
	}

	[Theory]
	[InlineData(WorkflowColumn.Backlog, WorkflowColumn.Build)]
	[InlineData(WorkflowColumn.Backlog, WorkflowColumn.AgentReview)]
	[InlineData(WorkflowColumn.Backlog, WorkflowColumn.HumanReview)]
	[InlineData(WorkflowColumn.Backlog, WorkflowColumn.Done)]
	[InlineData(WorkflowColumn.Ready, WorkflowColumn.AgentReview)]
	[InlineData(WorkflowColumn.Ready, WorkflowColumn.HumanReview)]
	[InlineData(WorkflowColumn.Ready, WorkflowColumn.Done)]
	[InlineData(WorkflowColumn.Build, WorkflowColumn.HumanReview)]
	[InlineData(WorkflowColumn.Build, WorkflowColumn.Done)]
	[InlineData(WorkflowColumn.AgentReview, WorkflowColumn.Done)]
	public void IsAllowedTransition_RejectsForwardJumps(WorkflowColumn from, WorkflowColumn to)
	{
		Assert.False(rules.IsAllowedTransition(from, to));
	}

	[Theory]
	[InlineData(WorkflowColumn.Ready, WorkflowColumn.Backlog)]
	[InlineData(WorkflowColumn.Build, WorkflowColumn.Backlog)]
	[InlineData(WorkflowColumn.Build, WorkflowColumn.Ready)]
	[InlineData(WorkflowColumn.AgentReview, WorkflowColumn.Backlog)]
	[InlineData(WorkflowColumn.AgentReview, WorkflowColumn.Ready)]
	[InlineData(WorkflowColumn.HumanReview, WorkflowColumn.Backlog)]
	[InlineData(WorkflowColumn.HumanReview, WorkflowColumn.Ready)]
	public void IsAllowedTransition_AllowsBackwardMovesToBacklogOrReady(WorkflowColumn from, WorkflowColumn to)
	{
		Assert.True(rules.IsAllowedTransition(from, to));
	}

	[Theory]
	[InlineData(WorkflowColumn.AgentReview, WorkflowColumn.Build)]
	[InlineData(WorkflowColumn.HumanReview, WorkflowColumn.Build)]
	public void IsAllowedTransition_AllowsBackwardMovesToBuildForRework(WorkflowColumn from, WorkflowColumn to)
	{
		Assert.True(rules.IsAllowedTransition(from, to));
	}

	[Fact]
	public void IsAllowedTransition_RejectsHumanReviewToAgentReview()
	{
		Assert.False(rules.IsAllowedTransition(WorkflowColumn.HumanReview, WorkflowColumn.AgentReview));
	}

	[Theory]
	[InlineData(WorkflowColumn.Done, WorkflowColumn.Backlog)]
	[InlineData(WorkflowColumn.Done, WorkflowColumn.Ready)]
	[InlineData(WorkflowColumn.Done, WorkflowColumn.Build)]
	[InlineData(WorkflowColumn.Done, WorkflowColumn.AgentReview)]
	[InlineData(WorkflowColumn.Done, WorkflowColumn.HumanReview)]
	[InlineData(WorkflowColumn.Done, WorkflowColumn.Done)]
	public void IsAllowedTransition_RejectsAnyMoveFromDone(WorkflowColumn from, WorkflowColumn to)
	{
		Assert.False(rules.IsAllowedTransition(from, to));
	}

	[Theory]
	[InlineData(WorkflowColumn.Backlog)]
	[InlineData(WorkflowColumn.Ready)]
	[InlineData(WorkflowColumn.Build)]
	[InlineData(WorkflowColumn.AgentReview)]
	[InlineData(WorkflowColumn.HumanReview)]
	[InlineData(WorkflowColumn.Done)]
	public void IsAllowedTransition_RejectsMoveToSameColumn(WorkflowColumn column)
	{
		Assert.False(rules.IsAllowedTransition(column, column));
	}

	[Fact]
	public void GetAllowedTransitions_ReturnsExpectedTransitionsForEachColumn()
	{
		Assert.Equal([WorkflowColumn.Ready], rules.GetAllowedTransitions(WorkflowColumn.Backlog));
		Assert.Equal([WorkflowColumn.Backlog, WorkflowColumn.Build], rules.GetAllowedTransitions(WorkflowColumn.Ready));
		Assert.Equal([WorkflowColumn.Backlog, WorkflowColumn.Ready, WorkflowColumn.AgentReview], rules.GetAllowedTransitions(WorkflowColumn.Build));
		Assert.Equal([WorkflowColumn.Backlog, WorkflowColumn.Ready, WorkflowColumn.Build, WorkflowColumn.HumanReview], rules.GetAllowedTransitions(WorkflowColumn.AgentReview));
		Assert.Equal([WorkflowColumn.Backlog, WorkflowColumn.Ready, WorkflowColumn.Build, WorkflowColumn.Done], rules.GetAllowedTransitions(WorkflowColumn.HumanReview));
		Assert.Empty(rules.GetAllowedTransitions(WorkflowColumn.Done));
	}

	[Fact]
	public void IsTerminal_IdentifiesDoneAsTerminal()
	{
		Assert.True(rules.IsTerminal(WorkflowColumn.Done));
		Assert.False(rules.IsTerminal(WorkflowColumn.Backlog));
		Assert.False(rules.IsTerminal(WorkflowColumn.Ready));
		Assert.False(rules.IsTerminal(WorkflowColumn.Build));
		Assert.False(rules.IsTerminal(WorkflowColumn.AgentReview));
		Assert.False(rules.IsTerminal(WorkflowColumn.HumanReview));
	}
}
