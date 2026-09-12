using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Infrastructure.Persistence;

namespace AgentTaskHarness.Infrastructure.Git;

public class LibGit2WorktreeService(AppDbContext dbContext) : IGitWorktreeService
{
}
