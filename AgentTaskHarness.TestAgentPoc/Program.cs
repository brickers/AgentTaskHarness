using AgentTaskHarness.TestAgentPoc.Components;
using AgentTaskHarness.TestAgentPoc.Hubs;
using AgentTaskHarness.TestAgentPoc.Pty;
using AgentTaskHarness.TestAgentPoc.TestAgent;

if (args.Contains("--smoke-test"))
{
    int ptyExitCode = await PtySmokeTest.RunAsync();
    if (ptyExitCode != 0)
    {
        Environment.Exit(ptyExitCode);
        return;
    }

    int agentExitCode = await TestAgentSmokeTest.RunAsync();
    Environment.Exit(agentExitCode);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
});

builder.Services.AddControllers();

// Core POSIX PTY Service
builder.Services.AddSingleton<IPtyService, PtyService>();

// Test Agent Session Manager & Lifecycle State Machine
builder.Services.AddSingleton<TestAgentSessionManager>();

// Inactivity Watchdog Service
builder.Services.AddSingleton<AgentWatchdogTimer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentWatchdogTimer>());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapControllers();
app.MapHub<TerminalHub>("/hubs/terminal");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
