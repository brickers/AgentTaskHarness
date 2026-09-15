# Agent Instructions

## Tool Usage & Rider MCP Priority

- **Favour Inbuilt Rider MCP Tools**: Always prefer Rider MCP tools (e.g., `build_solution_start`, `build_solution_state`, `findTests`, `execute_run_configuration`, `read_file`, `search_file`, `get_file_problems`, `list_directory_tree`) over custom shell/terminal commands (`dotnet build`, `dotnet test`, shell commands, etc.).
- Avoid running arbitrary terminal commands unless Rider MCP tools are incapable of performing the required action or diagnosing an issue.

## Test Execution & Failure Troubleshooting

When executing or debugging tests, adhere strictly to the following diagnostic sequence:

1. **Rider MCP First**: Initiate test runs using Rider MCP tools (`findTests`, run configurations).
2. **Verify Build on Silent/Empty Failure**: If a test run fails with no actionable output or diagnostics, check whether the solution/project builds cleanly first (using `build_solution_start` / `build_solution_state`).
3. **Fallback to Test Command**: If the build succeeds but the test run fails again with no actionable output, run a test command (e.g., CLI test runner) to capture detailed terminal/console diagnostics.
4. **Revert to Rider MCP**: Always return to Rider MCP calls as the default and primary mechanism for subsequent operations once the issue is understood.

## UI Styling & Component Design

- **Use Tailwind CSS Classes**: Style UI elements using Tailwind CSS utility classes.
- **Locate Styling in Components for Reusability**: Encapsulate styling directly within reusable components (e.g. shared Blazor components) to ensure modular, maintainable, and reusable UI elements across views, avoiding duplicated style declarations and ad-hoc global CSS.

## Temporary Files & Scratch Space

- **Use In-Project Temp Directory**: All temporary files, scratch scripts, logs, and intermediate artifacts must be placed inside the gitignored `.agent-temp` folder ([`AgentTaskHarness/AI/.agent-temp/`](file:///Users/gud/RiderProjects/AgentTaskHarness/AgentTaskHarness/AI/.agent-temp/)) rather than the system temp directory (e.g., `/tmp` or `$TMPDIR`).
- **Tool Visibility**: Storing temporary files within `.agent-temp` ensures they remain immediately discoverable, readable, and editable through Rider's built-in MCP file tools and the project view.
