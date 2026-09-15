# Agent Task Harness - Solution Design

## 1. Overview & Architecture

Agent Task Harness is a cross-platform .NET 10 / Blazor web application providing an automated orchestration environment for AI coding agents (such as GitHub Copilot CLI) executing against dedicated Git repositories.

### Key Architectural Pillars
- **Persistence**: Local SQLite database via EF Core with code-first migrations.
- **Git Operations**: Worktree and branch lifecycle isolation using `LibGit2Sharp`.
- **Agent Execution**: Headless command-line invocation via `CopilotCliProcessRunner` with bi-directional interactive in-app terminal streaming, OS environment sandboxing, execution context folder generation, and execution lifetime tracking.
- **Agent Integration (MCP)**: Embedded, in-process Model Context Protocol (MCP) server providing agents with runtime context and curated workflow transition actions.
- **UI Platform**: Interactive Blazor Server with Tailwind CSS design system, responsive layouts, universal toast notifications, and embedded interactive terminal emulator.

---

## 2. Information Architecture & Hierarchy

The application hierarchy consists of three core levels:
```
Workspace / Board  (1 git repository)
   └── Ticket (Feature)  (Parent deliverable: requirements, acceptance criteria, solution notes)
        └── Task (Step)   (Granular execution item: dev plan step worked on by agents)
```
*(Note: The legacy Roadmap view—both graph and plan representations—has been completely decommissioned and removed from the application).*

### Global Top-Level Entities
To ensure maximum reusability across projects, the following entities are configured globally at the top level rather than being scoped to a single board:
1. **Agents (`/agents`)**: Global agent definitions with Copilot-compatible YAML frontmatter data, prompts, guardrails, and composed reusable prompt components.
2. **Workflows (`/workflows`)**: Global task workflow templates defining custom stages, assigned agents, and named transitions. Boards select which global workflow to apply to their tasks.

---

## 3. Workflows: Tickets vs. Tasks

The system enforces a strict operational separation between Tickets and Tasks. Agents operate solely on Tasks; humans operate on Tickets.

```mermaid
flowchart TD
    subgraph TicketWorkflow["Ticket (Feature) Workflow - Fixed Human Lifecycle"]
        T_Backlog["Backlog"] -->|Ready to build (Sets StartedAt)| T_Build["Build"]
        T_Build -->|All child tasks Done| T_HumanReview["Human Review"]
        T_HumanReview -->|Approve| T_Done["Done (Terminal)"]
        T_Build -.->|Return to Backlog - Prompt worktree discard/pause| T_Backlog
    end

    subgraph TaskWorkflow["Task (Step) Workflow - User-Defined Agent Lifecycle"]
        S_NotStarted["Not Started"] -->|Ticket in Build & deps met (Sets StartedAt)| S_Entry["Workflow Entry Point"]
        subgraph UserDefined["User-Defined Workflow Stages"]
            S_Entry -->|Action A| S_Stage1["Stage 1 (Agent 1)"]
            S_Stage1 -->|Action B| S_Stage2["Stage 2 (Agent 2)"]
            S_Stage2 -->|Rework| S_Entry
        end
        UserDefined -->|Complete| S_Gate{"Skip Human Review?"}
        S_Gate -->|No / Card Requires| S_HumanReview["Human Review"]
        S_Gate -->|Yes| S_Done["Done (Terminal)"]
        S_HumanReview -->|Approve| S_Done
        S_HumanReview -.->|Reject / Send back| S_Entry
    end
```

### 3.1 Ticket (Feature) Workflow
Tickets do not have an agent workflow. Agents never operate directly on a ticket.
- **Stages**: `Backlog` → `Build` → `Human Review` → `Done`.
- **Entry & Lifecycle**:
  - Tickets are created in the `Backlog` column.
  - Moving a ticket to `Build` automatically creates the ticket's Git branch off `main` and dedicated worktree, and records its `StartedAt = DateTimeOffset.UtcNow`.
  - Moving a ticket to `Build` makes all of its child tasks available for the scheduler to pick up.
  - If a ticket is moved from `Build` back to `Backlog`, the application prompts the user to choose whether to **discard/delete** its Git branch and worktree (including all child task branches/worktrees) or **pause/keep** them for later resumption.
  - When all child tasks of a ticket reach `Done`, the ticket automatically advances from `Build` to `Human Review`.
  - Reaching `Done` merges the ticket's branch into `main` and cleans up its worktree.

### 3.2 Task (Step) Workflow
Tasks follow a flexible, user-defined pipeline where AI agents execute work:
- **Stages**: `Not Started` → `{{User Defined Workflow}}` → `Human Review` → `Done`.
- **Lifecycle & Scheduler Pickup**:
  - Tasks are created in `Not Started` nested under their parent ticket.
  - When the parent ticket enters `Build`, any task whose prerequisites are complete becomes eligible for execution.
  - When picked up by the scheduler, the task records `StartedAt = DateTimeOffset.UtcNow` (if not previously set) and advances into the designated **Entry Point** column of the board's active workflow, starting the assigned agent.
  - Tasks move between custom stages via transitions triggered by agents (via MCP) or human operators.
  - When transitioning towards completion, the workflow engine checks board settings (`SkipStepHumanReview`) and card override (`AlwaysRequireHumanReview`):
    - If Human Review is required: the task moves to `Human Review`.
    - If Human Review is skipped: the task moves directly to `Done`.
  - Reaching `Done` merges the task's branch into the parent ticket's branch, cleans up the task's worktree, and marks the task immutable.

### 3.3 Task Workflow Editor
Workflows are defined in a dedicated, sole-purpose **Task Workflow Editor** located globally at `/workflows`:
- Users define columns (stages) that make up the custom pipeline.
- Users must designate **at least one column as the Entry Point** for the workflow.
- **Every column in the workflow must have exactly one agent assigned**. (Match criteria is removed; assignment is direct 1:1 per stage).
- Users define transitions between columns and assign each transition an **Action Name** (e.g., `SubmitForReview`, `RequestRework`, `Approve`, `RunTests`).
- When an agent queries MCP for available actions on a task's current column, the harness returns the action names configured on that column's outgoing transitions.

---

## 4. Universal Dependency Management

Dependencies can be established both between Tickets and between Tasks:
- **Scope**:
  - **Ticket Dependencies**: Scoped to the same board. A ticket cannot move from `Backlog` to `Build` until all prerequisite tickets are `Done`.
  - **Task Dependencies**: Scoped to the same ticket. A task cannot leave `Not Started` until all prerequisite tasks within the same ticket are `Done`.
- **Bidirectional Editing & Symmetrical Views**:
  - Both sides of a dependency relationship share an identical, symmetrical view.
  - Viewing Item A displays both **Prerequisites (Blocked By / Depends On)** and **Dependents (Blocking / Depended On By)**.
  - Users can add or remove relationships from either side. Adding B as a dependent of A creates the exact same relationship as opening B and adding A as a prerequisite.
  - Cycle detection prevents circular dependencies across both creation and editing flows.
- **Creation-Time Dependency Assignment**:
  - Ticket and task creation dialogs allow selecting prerequisite dependencies directly at creation time via a multi-select selector.

---

## 5. Agent Definitions, Context Folder & Embedded Prompts

### 5.1 Frontmatter & Prompt Composition
Agent definitions match the standard GitHub Copilot agent architecture:
- **Storage**: Global SQLite entity `AgentDefinition` decoupled from boards.
- **Copilot Frontmatter Data**: Dedicated form fields for `Name`, `Description`, `Model` (e.g. `gpt-4o`, `claude-3.5-sonnet`), and `Tools` (CLI/MCP tool configurations).
- **Prompt Body & Component Insertion**: Dedicated prompt textarea with UI buttons to insert reusable prompt components (`AgentComponent`, e.g. repository conventions, testing standards, git guardrails).

### 5.2 Embedded Harness System Prompt
To guarantee seamless autonomous interaction with the harness, every agent invocation automatically embeds a standardized **Harness System Prompt** into its execution context:
1. **Context & Identity**: Informs the agent of its current `StepId` (Task), `FeatureId` (Ticket), and working directory (dedicated worktree).
2. **Harness MCP Protocol**: Informs the agent that the harness MCP server is active, exposing tools such as `get_available_actions(cardType, cardId)` and action execution tools.
3. **Completion Rule**: Once the agent has completed coding and verification for the task, it **must** query `get_available_actions` for its current task column.
4. **Action Selection**: The agent evaluates the available action names against its instructions/task requirements and invokes the matching action (e.g., `SubmitForReview`, `RequestRework`, `RunTests`).
5. **Mismatch & User Clarification**: If no returned action matches what the agent was instructed to do, or if there is ambiguity among multiple actions, the agent **must pause, prompt the user in the chat/terminal window, and ask which action to use**. Once the user responds, the agent calls the chosen action.

### 5.3 Agent Setup & Execution Context Folder Generation
When an agent is prepared and launched to execute on a task, the harness generates a dedicated **Agent Execution Context Directory** on disk:
- **Location**: Created in the card's worktree under `.agent-context/` (or dedicated harness run directory) isolated from the main repository.
- **Contents of Context Directory**:
  1. **Agent Definition & Instructions (`copilot-instructions.md` / `agent.md`)**:
     - Formatted with standard YAML frontmatter (`name`, `description`, `model`, `tools`).
     - Contains the **Embedded Harness System Prompt**.
     - Contains the composed prompt body with injected `AgentComponent` blocks.
  2. **Task Specification File (`TASK_CONTEXT.md`)**:
     - Task Title, Description, Guidance notes.
     - Parent Ticket Title, Requirements, Acceptance Criteria, Suggested Solution.
  3. **Harness MCP Configuration (`mcp_config.json`)**:
     - Configures the MCP client within Copilot CLI to connect directly to the Agent Task Harness MCP server.
  4. **Git Guard Shim Directory**:
     - Symlinked/written directory containing the read-only git wrapper scripts prepended to `PATH`.
- **Process Launch Parameterization**:
  - `CopilotCliProcessRunner` passes `--config-dir` or `--instructions-file` pointing to this generated context directory and sets `WorkingDirectory` to the card's worktree.

---

## 6. Execution Environment & Interactive In-App Terminal

### 6.1 Worktree Sandboxing & Environment
- Agents execute inside dedicated Git worktrees created via `LibGit2Sharp`.
- The `PATH` environment is prepended with a Git guard shim that blocks destructive Git commands (`push`, `checkout -b`, `rebase`).

### 6.2 Interactive In-App Terminal
External macOS Terminal windows (`Terminal.app`) and external `copilot://` deep links are completely eliminated in favor of a full in-app terminal experience:
- **Feasibility & Architecture**: Fully supported via bi-directional standard I/O redirection and interactive web terminal emulation.
  - **Backend Process**: The agent CLI process launches headlessly with redirected `StandardInput`, `StandardOutput`, and `StandardError`.
  - **Frontend Console (Xterm.js)**: Embedded inside `CardPopup.razor` under a dedicated "Terminal" tab using a standard terminal emulator (Xterm.js) via Blazor JSInterop.
  - **Bi-directional Streaming**:
    - **Output Stream**: Process `stdout` and `stderr` are streamed in real time to the browser via SignalR / Blazor events, rendering full ANSI colors, progress spinners, and formatted diffs.
    - **Interactive Input Stream**: Keystrokes, text input, tool approvals (`[y/n]` prompts), and chat window prompts typed into the in-app terminal are piped directly into `process.StandardInput`.
  - **Tool Approvals & Chat In-App**: Users can approve agent tool executions and interact directly in the conversation window inside the application UI without ever leaving the browser.

---

## 7. Agent Scheduler: Prioritization & Concurrency

### 7.1 Scheduler Activation
The scheduler evaluates and dispatches work:
- Periodically on a background timer loop.
- Reactively whenever a ticket moves to `Build` or a task is created.
- Reactively whenever an agent finishes, freeing a concurrency slot.
- Reactively whenever an agent is assigned to a column containing idle tasks.

### 7.2 Eligibility Criteria
A task is eligible for scheduler pickup if and only if all of the following conditions are met:
1. **Parent Ticket in Build**: The parent Ticket is in the `Build` column.
2. **Column Criteria**: The task is in **any column that is NOT in `Human Review` and NOT in `Done`** (i.e. `Not Started` or any user-defined workflow stage).
3. **No Active Agent**: The task must **not have an agent currently working on it** (`Status != Working && Status != WaitingForInput`).
4. **Dependencies Met**: All prerequisite tasks within the same ticket have reached `Done`.
5. **Threshold Check**: The task has not exceeded the board's review failure threshold.

### 7.3 Prioritization Algorithm
When available concurrency slots exist (`Board.ConcurrencyLimit - ActiveRunningCount > 0`), the scheduler selects which task to start using the following multi-tiered prioritization logic:

1. **Primary Sort: Ticket Selection**
   - **Criterion A (`StartedAt`)**: Tickets with `StartedAt != null` are prioritized first, ordered ascending by `StartedAt` (oldest started ticket first).
   - **Criterion B (Dependency Count Fallback)**: For tickets where `StartedAt` is not available (or when tying):
     - Prioritize by **number of dependencies** (`DependedOnBy.Count`), choosing the one with the most (highest count first).
   - **Criterion C (Random Tie-Breaker)**: If there is still no clear winner after sorting by number of dependencies, **choose at random** (`Random.Shared.Next()` / `Guid.NewGuid()`).

2. **Secondary Sort: Task Selection within the Chosen Ticket**
   - **Criterion A (`StartedAt`)**: Tasks with `StartedAt != null` are prioritized first, ordered ascending by `StartedAt` (oldest started task first).
   - **Criterion B (Dependency Count Fallback)**: For tasks where `StartedAt` is not available (or when tying):
     - Prioritize by **number of dependencies** (`DependedOnBy.Count`), choosing the one with the most dependents (highest count first).
   - **Criterion C (Random Tie-Breaker)**: If there is still no clear winner after sorting by number of dependencies, **choose at random**.

*(Note: Future enhancements will evaluate the transitive downstream set of dependencies across tickets, but the immediate implementation evaluates direct dependency count with a random tie-breaker).*

### 7.4 Dispatch & Pickup Action
For the top prioritized eligible tasks up to the available concurrency slots:
1. If the task is in `Not Started`, advance it to the board's designated **Entry Point** column in the active workflow and set `Task.StartedAt = DateTimeOffset.UtcNow`.
2. Generate the **Agent Execution Context Directory** (agent instructions, MCP config, task context, git shim).
3. Initialize an `AgentRun` entity with status `Working`.
4. Launch the assigned agent using `CopilotCliProcessRunner` pointing to the context directory and worktree.

---

## 8. UI, Layout & Design System

- **Styling Framework**: Standard Tailwind CSS utilities throughout the entire app. Elimination of custom CSS and inline style overrides.
- **Standardized Button Component (`<AppButton>`)**:
  - Uniform button styling across all views (Board, Settings, Agents, Workflows, Popups).
  - Supports variants: `Primary`, `Secondary`, `Danger`, `Ghost`.
  - Supports sizes: `sm`, `md`, `lg`.
  - Manages disabled states and loading indicators consistently.
- **Global Header & Navigation**:
  - **Top Navbar**: Left wordmark / icon linking to `/boards`. Top-level navigation items: **Projects (Boards)**, **Agents**, and **Workflows**.
  - **Top Right**: Clean and uncluttered. Branding text ("Agent Task Harness") removed from the top right.
  - **Breadcrumbs**: Located at the top of the page below the navbar. Contextually displays:
    - `/boards`: `Projects`
    - `/boards/{id}`: `Projects / [Board Name] / Board`
    - `/boards/{id}/settings`: `Projects / [Board Name] / Settings`
    - `/agents`: `Agents`
    - `/agents/{id}`: `Agents / [Agent Name]`
    - `/workflows`: `Workflows`
- **Board UI Polish**:
  - The top "New feature" action button is removed. Tickets are created via a "+ Add Ticket" button located inside the Backlog column header.
  - Item count text ("total items", "dev plan steps") removed from section headers.
  - Kanban column drag-and-drop borders dynamically apply visual feedback outlines (`border-emerald-500 ring-2 ring-emerald-300` for valid drops, `border-rose-400 opacity-60` for invalid drops).
- **Card Popup & Status Polish**:
  - Status is displayed in only one place in the card popup header.
  - The embedded status explanation card and status legend are removed from the popup.
  - A collapsible **Status Legend** is positioned directly on the Kanban board (bottom corner), defaulting to closed/collapsed.
  - Tickets and tasks are editable directly inside the popup when idle. Form fields are disabled and locked whenever an agent is actively running on the card.
- **Universal Toast Notifications**:
  - Global toast notification service (`IToastService`) rendering at the bottom of the screen.
  - All user actions (creation, updating, deleting tickets, tasks, workflows, errors) display bottom toasts.
  - Moving cards between columns generates **zero notifications** (silent drag-and-drop transitions).

---

## 9. Implementation Status & Gap Analysis

| Requirement Area | Feature / Capability | Status | Implementation Summary / Gap |
| :--- | :--- | :---: | :--- |
| **Global Entities** | Global Agent Definitions | **Completed** | Migrated `AgentDefinition` off `BoardId`; global `/agents` routes. |
| | Copilot Frontmatter & Component Insertion | **Pending** | Needs frontmatter fields (`description`, `model`, `tools`) + prompt component insertion UI. |
| | Global Task Workflow Editor (`/workflows`) | **Pending** | Needs `WorkflowTemplate`, `WorkflowStage`, and `WorkflowTransition` entities, plus editor page. |
| **Workflows** | Ticket 4-Stage Lifecycle (`Backlog > Build > HR > Done`) | **Pending** | Currently shares 6-column enum with Step; needs decoupling from agent execution. |
| | Backlog Worktree Discard/Pause Dialog | **Pending** | Needs interactive confirmation modal when dragging ticket from Build back to Backlog. |
| | Task User-Defined Workflow Execution | **Pending** | Tasks currently hardcoded to 6-column enum; needs mapping to active workflow stages. |
| | Reactive Column Agent Pickup | **Pending** | `TriggerQueueForColumnAsync` on assignment change to immediately start idle cards. |
| **Scheduler** | Prioritization on StartedAt / Dependency Count / Random | **Pending** | Add `StartedAt` to `Feature` and `Step`; filter tasks in non-(HR/Done) columns with no active agent; order by `StartedAt`, fallback to `DependedOnBy.Count` desc, then random. |
| **Agent Setup** | Context Folder Generation (`copilot-instructions.md`, MCP config, task context) | **Pending** | Write structured context folder with YAML frontmatter, harness system prompt, task spec, and MCP configs before launch. |
| **Agent Prompting** | Embedded Harness System Prompt | **Pending** | Inject MCP action query, execution, and user-clarification protocol into agent prompt. |
| **Terminal** | Interactive In-App Terminal & Stdin Pipe | **Pending** | Web terminal (Xterm.js), bi-directional stdin/stdout streaming, tool approval & chat in UI. |
| **Navigation & Layout** | Top-Level Navigation (`Projects`, `Agents`, `Workflows`) | **Partially Done** | Top navbar and breadcrumbs exist; needs `Workflows` link and breadcrumb fix on `/agents`. |
| | Remove Top-Right Branding Text | **Pending** | `NavMenu.razor` currently displays title on the top right. |
| | Decommission Roadmap View Completely | **Pending** | `Roadmap.razor` and `/roadmap` routes/links must be deleted. |
| **Design System** | Tailwind CSS Default Look & Feel | **In Progress** | Replaced scoped CSS with Tailwind classes in several components. |
| | Unified `<AppButton>` Component | **Pending** | Needs reusable Blazor button component replacing raw `<button>` tags. |
| | Fix Kanban Drag & Drop Outline Styles | **Pending** | Need to bind `isValidTarget`/`isInvalidTarget`/`isOver` Tailwind outline classes to column elements. |
| | Board Header Polish (Remove New Feature & Counts) | **Pending** | Remove "+ New feature" top action & item count texts; move create button into Backlog column. |
| **Notifications** | Universal Toast Notification System | **Pending** | Replace inline banners with `IToastService` & bottom-screen `ToastContainer.razor`. |
| | Silence Column Transition Notifications | **Pending** | Remove `Notify(\"Moved...\", false)` calls in drag-and-drop drop handlers. |
| **Dependencies** | Symmetrical Bidirectional Dependency Service | **Pending** | Add `GetDependentsAsync`, `AddDependentAsync`, `RemoveDependentAsync` in Feature/Step services. |
| | Symmetrical Card Popup Dependency Tab (Both Cards) | **Pending** | Enable dependencies tab for Steps and render symmetrical Prerequisites vs Dependents panels. |
| | Creation-Time Dependency Picker | **Pending** | Add multi-select dropdown in Ticket and Task creation dialogs. |
| **Card Popup & Legend** | Single Status Display in Popup | **Pending** | Strip duplicate status displays and remove status explanation card from `CardPopup.razor`. |
| | Board-Level Collapsible Status Legend | **Pending** | Extract status legend into board component defaulting to closed. |
| | In-Popup Card Details Editing with Agent Lock | **Pending** | Add edit inputs for ticket/task properties with disabled state when agent is active. |
