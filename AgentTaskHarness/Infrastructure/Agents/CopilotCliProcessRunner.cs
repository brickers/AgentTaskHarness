using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Infrastructure.Git;

namespace AgentTaskHarness.Infrastructure.Agents;

/// <summary>
///     Infrastructure runner that launches copilot CLI inside the card's worktree path,
///     generates the git guard shim on PATH to enforce read-only git operations,
///     tracks process lifetime, and extracts token/time usage on completion.
/// </summary>
public class CopilotCliProcessRunner(GitGuardShimWriter shimWriter) : IAgentProcessRunner
{
	private static readonly Regex TokenRegex = new(
		@"(?:tokens?(?:_used)?|token_count|total_tokens)\s*[:=]?\s*(\d+)",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex TimeSpentRegex = new(
		@"(?:time_spent|duration|elapsed)\s*[:=]?\s*(\d+(?:\.\d+)?)\s*(s|sec|seconds|m|min|minutes|h|hours)?",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private readonly ConcurrentDictionary<int, ProcessTrackingInfo> _activeProcesses = new();

	public Task<AgentProcessResult> StartAsync(Step step, AgentDefinition definition,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(step);
		ArgumentNullException.ThrowIfNull(definition);

		var workingDirectory = step.WorktreePath ?? Directory.GetCurrentDirectory();
		var shimDir = shimWriter.CreateGitGuardShimDirectory();

		var process = new Process();
		var sessionId = Guid.NewGuid().ToString("N");
		var sessionLink = $"copilot://session/{sessionId}";
		var prompt = BuildAgentPrompt(definition);
		var useTerminalApp = OperatingSystem.IsMacOS() &&
		                     string.Equals(Environment.GetEnvironmentVariable("AGENT_TASK_HARNESS_OPEN_TERMINAL"), "true",
			                     StringComparison.OrdinalIgnoreCase);

		process.StartInfo = useTerminalApp
			? BuildTerminalStartInfo(prompt, workingDirectory, sessionId, shimDir)
			: BuildHeadlessStartInfo(prompt, workingDirectory, sessionId);

		// Prepend git guard shim directory to PATH
		var existingPath = Environment.GetEnvironmentVariable("PATH") ?? "";
		var pathSeparator = Path.PathSeparator;
		process.StartInfo.EnvironmentVariables["PATH"] = $"{shimDir}{pathSeparator}{existingPath}";

		int? processId = null;
		try
		{
			if (process.Start())
			{
				processId = process.Id;
				_activeProcesses[process.Id] = new ProcessTrackingInfo(
					process,
					step.Id,
					shimDir,
					DateTimeOffset.UtcNow,
					sessionLink
				);
			}
		}
		catch
		{
			// If copilot binary is not found on PATH or cannot start, clean up shim
			shimWriter.CleanupShimDirectory(shimDir);
			process.Dispose();
		}

		return Task.FromResult(new AgentProcessResult(processId, sessionLink));
	}

	private static ProcessStartInfo BuildHeadlessStartInfo(string prompt, string workingDirectory, string sessionId)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = "copilot",
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add("--prompt");
		startInfo.ArgumentList.Add(prompt);
		startInfo.ArgumentList.Add("--worktree");
		startInfo.ArgumentList.Add(workingDirectory);
		startInfo.ArgumentList.Add("--session");
		startInfo.ArgumentList.Add(sessionId);
		return startInfo;
	}

	private static ProcessStartInfo BuildTerminalStartInfo(string prompt, string workingDirectory, string sessionId,
		string shimDir)
	{
		var command = $"export PATH={ShellQuote(shimDir)}:$PATH && cd {ShellQuote(workingDirectory)} && copilot --prompt {ShellQuote(prompt)} --worktree {ShellQuote(workingDirectory)} --session {ShellQuote(sessionId)}";
		var script = $"tell application \"Terminal\" to do script \"{AppleScriptQuote(command)}\"";
		var startInfo = new ProcessStartInfo
		{
			FileName = "osascript",
			UseShellExecute = false,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add("-e");
		startInfo.ArgumentList.Add(script);
		return startInfo;
	}

	private static string BuildAgentPrompt(AgentDefinition definition)
	{
		var parts = new[] { definition.Prompt, definition.Instructions, definition.ToolConfiguration }
			.Where(part => !string.IsNullOrWhiteSpace(part));
		return string.Join(Environment.NewLine + Environment.NewLine, parts);
	}

	private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\\"'\\\"'")}'";
	private static string AppleScriptQuote(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

	public Task StopAsync(int processId, CancellationToken cancellationToken = default)
	{
		if (_activeProcesses.TryRemove(processId, out var info))
			try
			{
				if (info.Process is { HasExited: false }) info.Process.Kill(true);
				info.Process?.Dispose();
			}
			catch
			{
				// Process might already have exited
			}
			finally
			{
				if (info.ShimDir != null) shimWriter.CleanupShimDirectory(info.ShimDir);
			}

		return Task.CompletedTask;
	}

	/// <summary>
	///     Parses token and time usage from agent CLI output or summary log.
	///     Supports key-value text lines, JSON payloads, and regex matching.
	/// </summary>
	public static bool TryParseUsage(string output, out long tokensUsed, out TimeSpan timeSpent)
	{
		tokensUsed = 0;
		timeSpent = TimeSpan.Zero;

		if (string.IsNullOrWhiteSpace(output)) return false;

		var matched = false;

		// Try JSON parsing first
		try
		{
			var trimmed = output.Trim();
			if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
			{
				using var doc = JsonDocument.Parse(trimmed);
				var root = doc.RootElement;

				if (root.TryGetProperty("tokens_used", out var tokensProp) ||
				    root.TryGetProperty("tokens", out tokensProp) ||
				    root.TryGetProperty("total_tokens", out tokensProp))
					if (tokensProp.TryGetInt64(out var tokens))
					{
						tokensUsed = tokens;
						matched = true;
					}

				if (root.TryGetProperty("time_spent_seconds", out var timeProp) ||
				    root.TryGetProperty("duration_seconds", out timeProp))
				{
					if (timeProp.TryGetDouble(out var seconds))
					{
						timeSpent = TimeSpan.FromSeconds(seconds);
						matched = true;
					}
				}
				else if (root.TryGetProperty("time_spent", out timeProp) ||
				         root.TryGetProperty("duration", out timeProp))
				{
					if (TimeSpan.TryParse(timeProp.GetString(), out var ts))
					{
						timeSpent = ts;
						matched = true;
					}
				}

				if (matched) return true;
			}
		}
		catch
		{
			// Not JSON, continue to regex parsing
		}

		// Regex parsing for text lines
		var tokenMatch = TokenRegex.Match(output);
		if (tokenMatch.Success && long.TryParse(tokenMatch.Groups[1].Value, out var parsedTokens))
		{
			tokensUsed = parsedTokens;
			matched = true;
		}

		var timeMatch = TimeSpentRegex.Match(output);
		if (timeMatch.Success && double.TryParse(timeMatch.Groups[1].Value, out var val))
		{
			var unit = timeMatch.Groups[2].Value.ToLowerInvariant();
			timeSpent = unit switch
			{
				"m" or "min" or "minutes" => TimeSpan.FromMinutes(val),
				"h" or "hours" => TimeSpan.FromHours(val),
				_ => TimeSpan.FromSeconds(val)
			};
			matched = true;
		}

		return matched;
	}

	private record ProcessTrackingInfo(
		Process? Process,
		Guid StepId,
		string? ShimDir,
		DateTimeOffset StartedAt,
		string? SessionLink);
}