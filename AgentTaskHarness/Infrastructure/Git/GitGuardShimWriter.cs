using System.Runtime.InteropServices;

namespace AgentTaskHarness.Infrastructure.Git;

/// <summary>
///     Generates a per-OS "git guard" directory and shim script allow-listing only read-only
///     git subcommands for agent processes executing inside card worktrees.
/// </summary>
public class GitGuardShimWriter
{
	public static readonly IReadOnlySet<string> AllowedReadOnlyCommands =
		new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"diff",
			"log",
			"status",
			"show",
			"branch",
			"rev-parse",
			"cat-file",
			"ls-files",
			"version",
			"help",
			"--version",
			"--help"
		};

	/// <summary>
	///     Creates a temporary directory containing a guarded 'git' executable shim allow-listing only read-only commands.
	///     Returns the directory path to prepend to PATH.
	/// </summary>
	public string CreateGitGuardShimDirectory(string? baseTempDir = null)
	{
		var dir = Path.Combine(baseTempDir ?? Path.GetTempPath(), "git-guard-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			// Windows batch / cmd shim
			var cmdPath = Path.Combine(dir, "git.cmd");
			var batContent = @"@echo off
setlocal
set CMD=%1
if ""%CMD%""=="""" (
    git.exe --help
    exit /b 0
)
for %%A in (diff log status show branch rev-parse cat-file ls-files version help --version --help) do (
    if /I ""%CMD%""==""%%A"" (
        git.exe %*
        exit /b %ERRORLEVEL%
    )
)
echo [GIT GUARD] Blocked write git command: %CMD% 1>&2
exit /b 1
";
			File.WriteAllText(cmdPath, batContent);
		}
		else
		{
			// POSIX shell shim for macOS / Linux
			var scriptPath = Path.Combine(dir, "git");
			var scriptContent = @"#!/bin/sh
CMD=""$1""
if [ -z ""$CMD"" ]; then
    /usr/bin/git --help
    exit 0
fi

case ""$CMD"" in
    diff|log|status|show|branch|rev-parse|cat-file|ls-files|version|help|--version|--help)
        # Find the real git binary that is not in this shim directory
        REAL_GIT=$(which -a git | grep -v ""$(dirname ""$0"")"" | head -n 1)
        if [ -z ""$REAL_GIT"" ]; then
            REAL_GIT=""/usr/bin/git""
        fi
        exec ""$REAL_GIT"" ""$@""
        ;;
    *)
        echo ""[GIT GUARD] Blocked write git command: $CMD"" >&2
        exit 1
        ;;
esac
";
			File.WriteAllText(scriptPath, scriptContent);
			try
			{
				File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite |
				                                 UnixFileMode.UserExecute |
				                                 UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
				                                 UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
			}
			catch
			{
				// Best-effort for Unix file mode
			}
		}

		return dir;
	}

	/// <summary>
	///     Cleans up a generated shim directory.
	/// </summary>
	public void CleanupShimDirectory(string shimDir)
	{
		try
		{
			if (Directory.Exists(shimDir)) Directory.Delete(shimDir, true);
		}
		catch
		{
			// Best-effort cleanup
		}
	}
}