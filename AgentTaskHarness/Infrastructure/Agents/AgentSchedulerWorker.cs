using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Infrastructure.Agents;

/// <summary>
/// Re-evaluates queued agent work independently of UI or MCP requests.
/// </summary>
public sealed class AgentSchedulerWorker(IServiceScopeFactory scopeFactory, ILogger<AgentSchedulerWorker> logger)
	: BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
		while (await timer.WaitForNextTickAsync(stoppingToken))
		{
			try
			{
				using var scope = scopeFactory.CreateScope();
				var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
				var scheduler = scope.ServiceProvider.GetRequiredService<IAgentScheduler>();
				var boardIds = await dbContext.Boards.Select(board => board.Id).ToListAsync(stoppingToken);

				foreach (var boardId in boardIds)
					await scheduler.ProcessQueueAsync(boardId, stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception exception)
			{
				logger.LogError(exception, "Agent scheduler worker iteration failed.");
			}
		}
	}
}
