using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Application.Comments;
using AgentTaskHarness.Application.Features;
using AgentTaskHarness.Application.Reviews;
using AgentTaskHarness.Application.Steps;
using AgentTaskHarness.Application.Usage;
using AgentTaskHarness.Application.Workflow;
using AgentTaskHarness.Components;
using AgentTaskHarness.Infrastructure.Agents;
using AgentTaskHarness.Infrastructure.Git;
using AgentTaskHarness.Infrastructure.Mcp;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
	.AddInteractiveServerComponents();

builder.Services.AddDbContext<AppDbContext>(options =>
	options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped<BoardService>();
builder.Services.AddScoped<FeatureService>();
builder.Services.AddScoped<StepService>();
builder.Services.AddSingleton<WorkflowTransitionRules>();
builder.Services.AddScoped<FeatureDependencyService>();
builder.Services.AddScoped<StepDependencyService>();
builder.Services.AddScoped<CommentService>();
builder.Services.AddScoped<ReviewOutcomeService>();
builder.Services.AddScoped<FeatureTransitionOrchestrator>();
builder.Services.AddScoped<StepTransitionOrchestrator>();
builder.Services.AddScoped<IAgentDefinitionFolderWriter, AgentDefinitionFolderWriter>();
builder.Services.AddScoped<AgentDefinitionService>();
builder.Services.AddScoped<AgentMatchingService>();
builder.Services.AddScoped<IGitWorktreeService, LibGit2WorktreeService>();
builder.Services.AddSingleton<GitGuardShimWriter>();
builder.Services.AddScoped<IAgentProcessRunner, CopilotCliProcessRunner>();
builder.Services.AddScoped<IAgentScheduler, AgentSchedulerService>();
builder.Services.AddHostedService<AgentSchedulerWorker>();
builder.Services.AddScoped<UsageTrackingService>();

builder.Services.AddScoped<BoardMcpTools>();
builder.Services.AddMcpServer()
	.WithHttpTransport()
	.WithToolsFromAssembly();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
	var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
	dbContext.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
	app.UseExceptionHandler("/Error", true);
	// The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/dotnet-support-policy.
	app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
	.AddInteractiveServerRenderMode();

app.MapMcp();

app.Run();