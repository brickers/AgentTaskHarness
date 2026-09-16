using AgentTaskHarness.TestAgentPoc.Components;
using AgentTaskHarness.TestAgentPoc.Pty;

if (args.Contains("--smoke-test"))
{
    int exitCode = await PtySmokeTest.RunAsync();
    Environment.Exit(exitCode);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// POSIX PTY Service for interactive test agent sessions
builder.Services.AddSingleton<IPtyService, PtyService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
