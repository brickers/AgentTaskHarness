namespace AgentTaskHarness.TestAgentPoc.Pty;

/// <summary>
/// Service implementation for managing and launching pseudo-terminal process sessions.
/// </summary>
public sealed class PtyService : IPtyService
{
    public IPtySession StartSession(PtyStartInfo startInfo)
    {
        return PtySession.Start(startInfo);
    }

    public IPtySession StartCopilotSession(
        string targetDir,
        string prompt,
        int cols = 80,
        int rows = 24,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        return PtySession.StartCopilot(targetDir, prompt, cols, rows, environmentVariables);
    }
}
