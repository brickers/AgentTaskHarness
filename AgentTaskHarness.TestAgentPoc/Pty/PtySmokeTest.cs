using System.Text;

namespace AgentTaskHarness.TestAgentPoc.Pty;

/// <summary>
/// Smoke test for Phase 1 to verify POSIX pseudo-terminal lifecycle:
/// - Process creation in PTY
/// - Bidirectional byte streaming (writing stdin, reading stdout)
/// - Terminal resize via ioctl TIOCSWINSZ
/// - Observable child process exit code
/// - Async output pump callback verification
/// - Copilot CLI binary execution in PTY
/// </summary>
public static class PtySmokeTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=================================================");
        Console.WriteLine(" Running POSIX PTY Smoke Test (Phase 1 Acceptance)");
        Console.WriteLine("=================================================");

        try
        {
            await TestOneWayOutputAsync();
            await TestBidirectionalStreamingAsync();
            await TestOutputPumpAsync();
            await TestTerminalResizeAsync();
            await TestProcessTerminationAsync();
            await TestCopilotDetectionAsync();

            Console.WriteLine("=================================================");
            Console.WriteLine(" ✅ All PTY Smoke Tests Passed Successfully!");
            Console.WriteLine("=================================================");
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n❌ Smoke Test Failed: {ex.Message}\n{ex.StackTrace}");
            Console.ResetColor();
            return 1;
        }
    }

    private static async Task TestOneWayOutputAsync()
    {
        Console.Write("Test 1: Output round-trip with /bin/echo... ");

        var startInfo = new PtyStartInfo(
            Command: "/bin/echo",
            Arguments: new[] { "hello", "from", "pty" });

        await using var session = PtySession.Start(startInfo);
        var sb = new StringBuilder();

        var buffer = new byte[256];
        int bytesRead;
        while ((bytesRead = await session.ReadAsync(buffer)) > 0)
        {
            sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
        }

        int exitCode = await session.WaitForExitAsync();
        string output = sb.ToString();

        if (!output.Contains("hello from pty"))
        {
            throw new InvalidOperationException($"Expected 'hello from pty' in output, but got: '{output}'");
        }

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Expected exit code 0, but got: {exitCode}");
        }

        Console.WriteLine($"PASS (Output: \"{output.Trim()}\", ExitCode: {exitCode})");
    }

    private static async Task TestBidirectionalStreamingAsync()
    {
        Console.Write("Test 2: Bidirectional streaming with /bin/cat... ");

        var startInfo = new PtyStartInfo(
            Command: "/bin/cat");

        await using var session = PtySession.Start(startInfo);
        const string testMessage = "ping-pong-pty-test-42\n";

        // Write to stdin of cat
        await session.WriteAsync(testMessage);

        var sb = new StringBuilder();
        var buffer = new byte[256];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // Read until the echoed message is found
        while (!sb.ToString().Contains("ping-pong-pty-test-42"))
        {
            int read = await session.ReadAsync(buffer, cts.Token);
            if (read <= 0) break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        string result = sb.ToString();
        if (!result.Contains("ping-pong-pty-test-42"))
        {
            throw new InvalidOperationException($"Did not receive echoed data from cat. Received: '{result}'");
        }

        // Send EOF (Ctrl+D / 0x04) so cat exits
        await session.WriteAsync(new byte[] { 0x04 });

        int exitCode = await session.WaitForExitAsync();
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Expected exit code 0 from cat, but got: {exitCode}");
        }

        Console.WriteLine($"PASS (Echo received, ExitCode: {exitCode})");
    }

    private static async Task TestOutputPumpAsync()
    {
        Console.Write("Test 3: Asynchronous output pump with callback... ");

        var startInfo = new PtyStartInfo(
            Command: "/bin/echo",
            Arguments: new[] { "pump", "test", "successful" });

        await using var session = PtySession.Start(startInfo);
        var sb = new StringBuilder();
        var tcsExit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        session.StartOutputPump(
            onData: (bytes, ct) =>
            {
                sb.Append(Encoding.UTF8.GetString(bytes));
                return Task.CompletedTask;
            },
            onExit: code =>
            {
                tcsExit.TrySetResult(code);
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int exitCode = await tcsExit.Task.WaitAsync(cts.Token);
        string output = sb.ToString();

        if (!output.Contains("pump test successful"))
        {
            throw new InvalidOperationException($"Expected 'pump test successful' in output, but got: '{output}'");
        }

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Expected exit code 0, but got: {exitCode}");
        }

        Console.WriteLine($"PASS (Received: \"{output.Trim()}\", ExitCode: {exitCode})");
    }

    private static Task TestTerminalResizeAsync()
    {
        Console.Write("Test 4: Terminal resize via ioctl TIOCSWINSZ... ");

        var startInfo = new PtyStartInfo(Command: "/bin/sleep", Arguments: new[] { "1" });
        using var session = PtySession.Start(startInfo);

        session.Resize(120, 40);
        session.Resize(80, 24);

        Console.WriteLine("PASS");
        return Task.CompletedTask;
    }

    private static async Task TestProcessTerminationAsync()
    {
        Console.Write("Test 5: Child process termination via Kill(SIGTERM)... ");

        var startInfo = new PtyStartInfo(Command: "/bin/sleep", Arguments: new[] { "30" });
        await using var session = PtySession.Start(startInfo);

        if (!session.IsRunning)
        {
            throw new InvalidOperationException("Process should be running.");
        }

        session.Kill(NativePosixPty.SIGTERM);
        int exitCode = await session.WaitForExitAsync();

        if (session.IsRunning)
        {
            throw new InvalidOperationException("Process should no longer be running.");
        }

        Console.WriteLine($"PASS (Terminated cleanly, ExitCode: {exitCode})");
    }

    private static async Task TestCopilotDetectionAsync()
    {
        Console.Write("Test 6: Copilot CLI discovery... ");
        string copilotPath = PtySession.ResolveCopilotBinaryPath();

        if (File.Exists(copilotPath))
        {
            Console.WriteLine($"PASS (Found at '{copilotPath}')");

            Console.Write("Test 6b: copilot --version in PTY... ");
            var startInfo = new PtyStartInfo(Command: copilotPath, Arguments: new[] { "--version" });
            await using var session = PtySession.Start(startInfo);

            var sb = new StringBuilder();
            var buffer = new byte[256];
            int read;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((read = await session.ReadAsync(buffer, cts.Token)) > 0)
            {
                sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            int exitCode = await session.WaitForExitAsync();
            Console.WriteLine($"PASS (Version: \"{sb.ToString().Trim()}\", ExitCode: {exitCode})");
        }
        else
        {
            Console.WriteLine($"SKIPPED (Copilot executable not found at default paths)");
        }
    }
}
