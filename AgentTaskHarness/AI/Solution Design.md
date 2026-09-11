# Agent Task Harness Solution Design

## Platform

- Cross-platform C# app; runs on Windows, Linux, and macOS.
- All board, column, task, dependency, and agent-definition data is persisted locally in SQLite.
- Uses Blazor for the web UI.

## Boards, Columns & Tasks

- Multiple, fully independent Kanban boards; each has its own columns, agents, and tasks, fully isolated from other boards.
- Columns can be created, reordered, and configured with allowed transitions.
- Web UI supports full CRUD (create, read, update, delete) for tasks.
- Every board has a terminal/"Done" column; agents cannot be defined for the terminal column.
- Every board has a backlog column. Agents cannot be defined for the backlog column.
- tasks can only be created in the backlog column.
- A task with a running agent cannot be deleted; the agent must be stopped first.

## Task Dependencies

- Task dependencies only apply within the same board.
- Tasks can depend on other tasks on the same board.
- A task can't start (leave the backlog) until all its dependencies are complete (merged to main).
- Once a task has started, its dependency list is locked; it can only be edited after moving the task back to the backlog.
- Completed (merged) tasks are terminal — they never move backward.
- Returning a started task to the backlog to edit dependencies lets the user choose to discard the branch/worktree or keep it paused for later resumption.
- If the branch/worktree was kept paused, resuming the task (leaving the backlog again) reuses that same branch/worktree rather than creating a new one.

## Interfaces & Access

- Boards are accessible to humans via a web UI and to agents via MCP.
- Via MCP, agents can read task details, move a task between columns, query the available/allowed moves for a task, and report a task's outcome as failed or succeeded.
- Single-user system; no authentication.

## Agent Scheduling & Concurrency

- Each column can have an assigned agent; entering the column invokes that agent on the task.
- A configurable, per-board limit caps how many agents can run concurrently.
- If a task changes column (via MCP) while its current agent is still running, the new column's agent is not started; the task is flagged (e.g. "blocked: previous agent still active") until the outgoing agent finishes or is manually stopped.
- Users are able to move tasks according to the allowed transitions defined for each column, but cannot move a task into a column if its agent is still running.
- If the per-board concurrency limit is already reached when a column transition would otherwise start a new agent, the task queues and its agent starts automatically once a slot frees up.
- On agent failure, the task is marked "Failed"; retry is manual only — no automatic retry.

## Agent Definition & Composition

- Agents are defined by composing reusable agent components (add, remove, reorder).
- Saving an agent definition in the UI generates a definition folder on disk with the composed configuration.

## Agent Execution Environment

- Agents run via the copilot CLI inside the task's dedicated git worktree, not the main repo checkout.
- The folder path is passed to the agent on invocation via the copilot CLI.
- Only read-only git commands are permitted (e.g. `git diff`, `git log`, `git status`); commit/push/checkout-modifying commands are blocked.

## Git / Version Control Workflow

- Each board matches a single git repo; each task gets its own branch and worktree, named after the task id, created automatically when the task first leaves the backlog.
- Every column transition triggers an automatic commit of pending changes in the task's worktree.
- Reaching the terminal/"Done" column merges the branch into main and deletes the worktree; the task then becomes immutable.
- If a merge conflict occurs, the ticket status indicates to the user that input is needed. The user can resolve the issues manually outside the harness, then click a button in the UI to resume the merge and complete the task.

## UI & Status Visibility

- When an agent is active on a task, its ticket shows a clickable link to the live copilot CLI session.
- A status icon reflects the agent's state: working, waiting for input, completed, failed, blocked, etc.

## Agent Control

- In the UI users can manually stop an agent, which kills the copilot CLI process.
- In the UI users can click a button to remove all uncommitted changes in the task's worktree.
