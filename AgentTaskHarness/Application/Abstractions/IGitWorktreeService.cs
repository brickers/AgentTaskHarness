namespace AgentTaskHarness.Application.Abstractions;

public interface IGitWorktreeService
{
}

public class GitMergeConflictException : InvalidOperationException
{
	public GitMergeConflictException() : base("Merge conflict detected. Resolve the conflict in the repository, then resume.")
	{
	}
}
