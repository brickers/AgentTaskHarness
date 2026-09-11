using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentTaskHarness.Infrastructure.Persistence;

public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
	public AppDbContext CreateDbContext(string[] args)
	{
		var options = new DbContextOptionsBuilder<AppDbContext>()
			.UseSqlite("Data Source=agent-task-harness.db")
			.Options;

		return new AppDbContext(options);
	}
}