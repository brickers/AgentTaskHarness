# Agent Task Harness - Issues 2 Comprehensive Implementation Plan

This development plan translates all requirements from `issues2.md`, user feedback, and the updated `Solution Design.md` into an actionable, phased engineering roadmap. It identifies what has been completed, specifies all new features and changes, and details acceptance criteria and implementation guidance for every task.

---

## 1. Executive Summary & Requirement Matrix

| # | Requirement from `issues2.md` & Design Updates | Target Area | Status | Implementation Phase |
| :--- | :--- | :--- | :---: | :--- |
| **1** | Agents defined at top level globally, decoupled from boards | Domain, EF, App | **Completed** | **Phase 1** (Committed in `33de5a8`) |
| **2** | Dedicated Board Settings page (`/boards/{id}/settings`) | Board UI & Routing | **Completed** | **Phase 1** (Committed in `33de5a8`) |
| **3** | Unified Breadcrumb component structure | Layout Components | **Partially Done** | **Phase 1: Navigation & Clean-up** |
| **4** | Fix Agents page breadcrumb (`Agents`, not `Projects / Agents`) | Shared Components | **Pending** | **Phase 1: Navigation & Clean-up** |
| **5** | Remove "Agent Task Harness" from top right | Layout & Navbar | **Pending** | **Phase 1: Navigation & Clean-up** |
| **6** | Decommission Roadmap view completely (graph & plan views) | Pages, Routes, Nav | **Pending** | **Phase 1: Navigation & Clean-up** |
| **7** | Board header clean-up: remove "New feature" button and item count text | Board View UI | **Pending** | **Phase 1: Navigation & Clean-up** |
| **8** | Add "+ Add Ticket" button inside Backlog column header | Board Kanban View | **Pending** | **Phase 1: Navigation & Clean-up** |
| **9** | Reusable `<AppButton>` component with Tailwind styles and properties | Design System | **Pending** | **Phase 2: Design System & Buttons** |
| **10** | Restore Kanban drag-and-drop outline highlight styles | Board CSS / HTML | **Pending** | **Phase 2: Design System & Buttons** |
| **11** | Universal Toast Notification system (bottom of screen) | UI Infrastructure | **Pending** | **Phase 3: Toast System** |
| **12** | Silence notifications on column transitions (no toast on drag/drop) | Orchestrators & UI | **Pending** | **Phase 3: Toast System** |
| **13** | Symmetrical bidirectional dependency editing (view both sides) | Services & Shared UI | **Pending** | **Phase 4: Universal Dependencies** |
| **14** | Set dependencies directly in Ticket and Task creation dialogs | Creation Modals | **Pending** | **Phase 4: Universal Dependencies** |
| **15** | Tasks have full dependency parity with tickets using shared logic/UI | Domain, App, UI | **Pending** | **Phase 4: Universal Dependencies** |
| **16** | Single status in popup; status legend on board (closed by default) | CardPopup & Board | **Pending** | **Phase 5: Popup & Board Polish** |
| **17** | Tickets and tasks editable in popup unless agent actively working | CardPopup UI & App | **Pending** | **Phase 5: Popup & Board Polish** |
| **18** | Copilot agent structure: frontmatter fields & prompt component insertion | Domain, App, UI | **Pending** | **Phase 6: Copilot Agent Structure** |
| **19** | Embedded Harness System Prompt (MCP actions protocol & user clarification) | Process Runner & Prompts | **Pending** | **Phase 6: Copilot Agent Structure** |
| **20** | Agent Setup & Execution Context Folder Generation (`.agent-context/`) | Agent Infrastructure | **Pending** | **Phase 6: Copilot Agent Structure** |
| **21** | Global Task Workflow Editor (`/workflows`) with named transitions & assigned agents | Domain, DB, UI | **Pending** | **Phase 7: Task Workflow Engine** |
| **22** | Ticket 4-stage lifecycle (`Backlog > Build > HR > Done`); tasks availability on Build | Orchestrators & Rules | **Pending** | **Phase 7: Task Workflow Engine** |
| **23** | Backlog return worktree pause/delete confirmation dialog | Git & Board UI | **Pending** | **Phase 7: Task Workflow Engine** |
| **24** | MCP agent tool integration returning transition action names | MCP Tools & App | **Pending** | **Phase 7: Task Workflow Engine** |
| **25** | Scheduler Prioritization: StartedAt -> Dep Count -> Random tie-breaker (non-HR/Done) | Scheduler Service | **Pending** | **Phase 7: Task Workflow Engine** |
| **26** | Reactive agent pickup when agent assigned to column with existing cards | Scheduler Worker | **Pending** | **Phase 8: Agent Execution & Terminal** |
| **27** | Interactive in-app terminal (bi-directional stdin/stdout, tool approvals & chat) | Process Runner & UI | **Pending** | **Phase 8: Agent Execution & Terminal** |

---

## Phase 1: Global Navigation, Breadcrumbs & Decommissioning Roadmap

### 1.1 Remove Roadmap Completely (Graph & Plan Views)
- **Problem**: Requirement explicitly states *"remove the roadmap completely - grraph and plan view"*. The code still contains `Roadmap.razor`, graph layout helpers, and roadmap navigation links.
- **Implementation Guidance**:
    1. Delete `Components/Pages/Roadmap/Roadmap.razor` and `Components/Pages/Roadmap/FeatureGraphLayout.cs`.
    2. Remove `@page "/boards/{BoardId:guid}/roadmap"` and all references to `/roadmap` in `AppBreadcrumb.razor`, `BoardSettings.razor`, and other components.
    3. Ensure navigation strictly provides **Projects** (`/boards`), **Agents** (`/agents`), and **Workflows** (`/workflows`).
- **Acceptance Criteria**:
    - All `/roadmap` routes are removed and attempting to navigate returns a 404 or redirects to `/boards`.
    - Breadcrumbs and navigation dropdowns no longer display "Roadmap", "Plan view", or "Graph view".
    - Codebase contains zero unused roadmap layout files.

### 1.2 Top-Level Navigation & Branding Clean-up
- **Problem**:
    - `NavMenu.razor` currently displays *"Agent Task Harness"* in the top right.
    - The top navigation lacks a direct link to the upcoming global **Workflows** editor.
- **Implementation Guidance**:
    1. In `NavMenu.razor`: Remove the top-right text "Agent Task Harness" and dot indicator. Keep top-right clear or dedicated to global status.
    2. Add **Workflows** (`/workflows`) alongside **Projects** (`/boards`) and **Agents** (`/agents`).
- **Acceptance Criteria**:
    - No "Agent Task Harness" branding appears in the top right of the application header.
    - Top-level menu provides clean links to Projects, Agents, and Workflows.

### 1.3 Universal Breadcrumb Display Fixes
- **Problem**: When visiting `/agents`, the breadcrumb displays `Projects / Agents` because the root `<details>` is hardcoded to "Projects".
- **Implementation Guidance**:
    1. In `Components/Shared/AppBreadcrumb.razor`:
        - If the route is `/agents` or `/agents/{id}`, the root breadcrumb should render `Agents` directly (or a dropdown allowing switching between Projects, Agents, and Workflows).
        - If the route is `/workflows` or `/workflows/{id}`, the breadcrumb should render `Workflows`.
        - When inside a board context (`/boards/{id}/...`), render:
          `Projects / [Board Name] / Board` or `Projects / [Board Name] / Settings`.
- **Acceptance Criteria**:
    - On the Agents page, the breadcrumb displays `Agents` (and `Agents / [Agent Name]` when editing), not `Projects / Agents`.
    - On the Workflows page, the breadcrumb displays `Workflows`.
    - On board pages, breadcrumb accurately indicates the current board and view (`Board` or `Settings`).

### 1.4 Board Header Polish & Backlog Ticket Creation
- **Problem**:
    - `Board.razor` contains a top action button "New feature" which needs to be removed.
    - Header text contains item count text `(@_features.Count total)` and `(@_selectedFeatureSteps.Count dev plan steps)` which must be removed.
    - Users need a designated place to create tickets with dependencies.
- **Implementation Guidance**:
    1. In `Board.razor`:
        - Remove `<Actions><button ...>New feature</button></Actions>` from `<PageTemplate>`.
        - Remove the item count badges `(@_features.Count total)` and `(@_selectedFeatureSteps.Count dev plan steps)`.
        - Place a `+ Add Ticket` button directly inside the header of the `Backlog` column for Features.
        - Keep `+ Add Step` / `+ Add Task` inside the `Not Started` column header or the selected ticket's task section.
- **Acceptance Criteria**:
    - The top "New feature" button in the board action bar is gone.
    - No item count text appears next to section titles.
    - The Backlog column header contains the "+ Add Ticket" button that opens the ticket creation dialog.

---

## Phase 2: Design System, Reusable Button Component & Drag-and-Drop Outlines

### 2.1 Reusable Button Component (`<AppButton>`)
- **Problem**: Mismatched button styles across pages (different padding, borders, colors, and raw HTML `<button>` tags).
- **Implementation Guidance**:
    1. Create `Components/Shared/AppButton.razor`:
       ```razor
       @code {
           [Parameter] public string Variant { get; set; } = "primary"; // primary, secondary, danger, ghost
           [Parameter] public string Size { get; set; } = "md"; // sm, md, lg
           [Parameter] public string? Type { get; set; } = "button";
           [Parameter] public bool Disabled { get; set; }
           [Parameter] public bool IsLoading { get; set; }
           [Parameter] public EventCallback<MouseEventArgs> OnClick { get; set; }
           [Parameter] public RenderFragment? ChildContent { get; set; }
           [Parameter] public string? AdditionalClass { get; set; }
       }
       ```
    2. Implement uniform Tailwind styling:
        - `primary`: `bg-indigo-600 hover:bg-indigo-700 text-white font-medium shadow-sm`
        - `secondary`: `bg-white hover:bg-slate-50 text-slate-700 font-medium border-[3px] border-slate-300 shadow-sm`
        - `danger`: `bg-red-600 hover:bg-red-700 text-white font-medium shadow-sm`
        - `ghost`: `text-slate-600 hover:bg-slate-100 font-medium`
        - Sizes: `sm` (`px-2.5 py-1.5 text-xs`), `md` (`px-3.5 py-2 text-sm`), `lg` (`px-4 py-2.5 text-base`).
    3. Refactor all buttons in `Board.razor`, `BoardSettings.razor`, `AgentDefinitionEditor.razor`, and `CardPopup.razor` to use `<AppButton>`.
- **Acceptance Criteria**:
    - All buttons throughout the application share identical Tailwind typography, border radius, padding, focus rings, and hover/active states.
    - Zero raw `<button class="inline-flex items-center...">` duplicates in page views.

### 2.2 Restore Drag-and-Drop Outline Highlight Styles
- **Problem**: "Drag and drop outlines are no longer showing" because `isValidTarget`, `isInvalidTarget`, and `isOver` boolean flags in `Board.razor` are not bound to CSS class names.
- **Implementation Guidance**:
    1. In `Board.razor`, update the Kanban column container `class` attribute:
       ```razor
       var outlineClass = isOver
           ? (isValidTarget ? "border-emerald-500 ring-2 ring-emerald-300 bg-emerald-50/50" : "border-rose-500 ring-2 ring-rose-300 bg-rose-50/50")
           : (isValidTarget ? "border-dashed border-indigo-400 bg-indigo-50/20" : (isInvalidTarget ? "border-slate-300 opacity-60" : "border-slate-400 bg-slate-50"));
       ```
    2. Ensure smooth CSS transitions (`transition-all duration-150`).
- **Acceptance Criteria**:
    - When dragging a card, all valid destination columns display a clear dashed highlight.
    - Hovering directly over a valid destination column displays an emerald outline and background tint.
    - Hovering over an invalid column displays a rose/red warning outline.

---

## Phase 3: Universal Toast Notification System

### 3.1 Toast Infrastructure (`IToastService` & `ToastContainer`)
- **Problem**: Notifications are scattered across top-right banners, inline alerts, and modal messages. Column movements trigger annoying popups.
- **Implementation Guidance**:
    1. Create `Application/Notifications/IToastService.cs` and `ToastService.cs`:
        - Methods: `ShowSuccess(string message)`, `ShowError(string message)`, `ShowInfo(string message)`.
        - Maintains thread-safe active toast list with 4-second auto-dismiss timers and manual close.
        - Event `event Action? OnChanged`.
    2. Register `IToastService` as Scoped in `Program.cs`.
    3. Create `Components/Shared/ToastContainer.razor` placed in `MainLayout.razor`:
        - Fixed position at the bottom of the screen: `fixed bottom-6 right-6 z-50 flex flex-col gap-2 pointer-events-none max-w-md w-full`.
        - Individual toast: `pointer-events-auto flex items-center justify-between p-4 rounded-lg shadow-xl text-sm font-medium border-[3px] transition-all`.
        - Success: `bg-emerald-50 text-emerald-900 border-emerald-300`
        - Error: `bg-rose-50 text-rose-900 border-rose-300`
        - Info: `bg-sky-50 text-sky-900 border-sky-300`
- **Acceptance Criteria**:
    - All actionable notifications (item created, item updated, item deleted, errors) display in a uniform toast at the bottom of the screen.
    - Toasts automatically dismiss after 4 seconds or when clicking the close button.

### 3.2 Notification Audit & Silencing Column Moves
- **Problem**: Column drag-and-drop movements currently show notifications like "Moved feature to Build".
- **Implementation Guidance**:
    1. In `Board.razor`, in `HandleFeatureDrop` and `HandleStepDrop`:
        - **Delete** `Notify($"Moved feature to {targetColumn}.", false);` and `Notify($"Moved step to {targetColumn}.", false);`.
        - Keep `try-catch` reporting errors to `ToastService.ShowError(ex.Message)`.
    2. Replace all remaining `_notificationMessage` banners in `Board.razor`, `BoardList.razor`, `AgentDefinitionEditor.razor`, and `CardPopup.razor` with `ToastService.ShowSuccess(...)`.
- **Acceptance Criteria**:
    - Dragging and dropping a card between columns produces **no toast notification**.
    - Creation, updating, and deletion of tickets, tasks, and workflows reliably show bottom-screen toasts.

---

## Phase 4: Symmetrical Bidirectional Dependencies & Creation Modals

### 4.1 Bidirectional Dependency Service Operations
- **Problem**: Dependencies can only be viewed and added from the prerequisite side ("Depends On"). The dependent side ("Depended On By") cannot add or remove links.
- **Implementation Guidance**:
    1. `FeatureDependencyService.cs`:
        - Implement `GetDependentsAsync(Guid featureId)`: Returns features that depend on `featureId` (`Where d.DependsOnFeatureId == featureId`).
        - Implement `AddDependentAsync(Guid featureId, Guid dependentFeatureId)`: Delegates to `AddAsync(dependentFeatureId, featureId)`.
        - Implement `RemoveDependentAsync(Guid featureId, Guid dependentFeatureId)`.
        - Ensure cycle detection checks run identically in both directions.
    2. `StepDependencyService.cs`:
        - Implement matching `GetDependentsAsync`, `AddDependentAsync`, and `RemoveDependentAsync`.
- **Acceptance Criteria**:
    - Given cards A and B, adding B as a dependent of A creates the exact same relationship as opening B and adding A as a prerequisite.
    - Cycle detection blocks circular links in either direction.

### 4.2 Symmetrical Dependency Section Component
- **Problem**: `CardPopup.razor` has no dependency management for Steps and only shows forward dependencies for Features.
- **Implementation Guidance**:
    1. Create `Components/Shared/CardDependencySection.razor`:
        - Reusable for both Tickets and Tasks.
        - Renders two symmetrical panels:
            - **Prerequisites (Depends On / Blocked By)**: Cards that must reach Done before this card can proceed.
            - **Dependents (Blocking / Depended On By)**: Cards waiting for this card to complete.
        - Each panel has:
            - Item list showing title, status icon, current column, and a remove button.
            - Dropdown of candidate cards with an "Add" button.
        - Disabled/locked when an agent is active on the card or card is in terminal state.
    2. Embed `<CardDependencySection />` into `CardPopup.razor` for **both** Features and Steps.
- **Acceptance Criteria**:
    - Both Tickets and Tasks provide a Dependencies tab in `CardPopup`.
    - Both sides of a dependency relationship have an identical, symmetrical view.
    - Dependencies can be added or removed from either side.

### 4.3 Set Dependencies on Ticket & Task Creation
- **Problem**: Dependencies can currently only be configured after creation.
- **Implementation Guidance**:
    1. In `Board.razor` Ticket Creation modal:
        - Add a multi-select dropdown for "Prerequisite Tickets" populated with existing Backlog/Ready tickets on the board.
        - On creation, persist the ticket and invoke `FeatureDependencyService.AddAsync` for each selected prerequisite.
    2. In Task Creation modal:
        - Add a multi-select dropdown for "Prerequisite Tasks" populated with existing tasks under the same ticket.
        - On creation, persist the task and invoke `StepDependencyService.AddAsync` for each selected prerequisite.
- **Acceptance Criteria**:
    - Creating a ticket allows selecting prerequisite tickets directly in the creation modal.
    - Creating a task allows selecting prerequisite sibling tasks directly in the creation modal.
    - Newly created cards show their dependencies immediately.

---

## Phase 5: Popup & Board Polish

### 5.1 Single Status Display & Board Collapsible Legend
- **Problem**: Status is displayed redundantly in multiple places in `CardPopup.razor` alongside an explanation card, cluttering the view.
- **Implementation Guidance**:
    1. In `CardPopup.razor`:
        - Display the status badge in only one place: in the header next to the title.
        - Remove the redundant status cards and remove the collapsible status legend from the popup.
    2. In `Board.razor`:
        - Create `Components/Shared/StatusLegend.razor`.
        - Position the legend in a compact drawer in the bottom corner of the board.
        - **Default state: closed/collapsed**.
        - Clicking toggles the legend to show status indicators (Working, Waiting for input, Queued, Completed, Failed, Blocked).
- **Acceptance Criteria**:
    - The status appears in exactly one place in `CardPopup`.
    - The status legend is absent from `CardPopup`.
    - The board contains a collapsible status legend that defaults to closed.

### 5.2 In-Popup Card Editing with Agent Running Lock
- **Problem**: Card details inside `CardPopup` cannot be edited in place.
- **Implementation Guidance**:
    1. In `CardPopup.razor`:
        - For Tickets: Editable form fields for Title, Requirements, Acceptance Criteria, and Suggested Solution.
        - For Tasks: Editable form fields for Title, Description, and Guidance Notes.
        - Provide a "Save Changes" `<AppButton>`.
        - If an agent is running on the card (`IsAgentRunning == true`):
            - Disable all input fields (`disabled="@IsAgentRunning"`).
            - Hide or disable the Save button.
            - Render a prominent warning banner: *\"Card is locked for editing while an agent is actively running.\"*
- **Acceptance Criteria**:
    - Idle tickets and tasks can be edited and saved directly in the popup.
    - Active cards with running agents strictly lock all editing fields.

---

## Phase 6: Copilot Agent Structure, Context Folder & Embedded Prompts

### 6.1 Copilot Frontmatter Model
- **Problem**: Agent definitions currently have simple text columns (`Prompt`, `Instructions`, `ToolConfiguration`) and do not follow standard Copilot agent structure.
- **Implementation Guidance**:
    1. Update `Domain/Entities/AgentDefinition.cs`:
        - Add `Description` (string).
        - Add `Model` (string, e.g. `gpt-4o`, `claude-3.5-sonnet`).
        - Add `Tools` (string, YAML/JSON formatted tool declarations).
        - Retain `Prompt` as the main markdown body.
    2. Create an EF Core migration `UpdateAgentDefinitionCopilotStructure`.
- **Acceptance Criteria**:
    - Agent definition entity stores Copilot-compatible frontmatter properties in the database.

### 6.2 Agent Editor with Frontmatter Fields & Component Insertion
- **Problem**: Users need an authoring experience matching Copilot structure with dedicated fields for frontmatter and prompt body with component insertion.
- **Implementation Guidance**:
    1. In `AgentDefinitionEditor.razor`:
        - Top section: Dedicated form fields for `Name`, `Description`, `Model`, and `Tools`.
        - Bottom section: Dedicated prompt body textarea.
        - Provide a component inserter: clicking an available `AgentComponent` inserts its reference or content into the prompt body at cursor position (or appends as a composed block).
- **Acceptance Criteria**:
    - Agent definition editor provides dedicated fields for frontmatter and prompt body.
    - Users can select and insert reusable prompt components into the agent definition.

### 6.3 Embedded Harness System Prompt (MCP Actions Protocol & User Clarification)
- **Problem**: Agents operating on tasks need standardized operational rules for querying workflow actions and knowing what to do when actions don't match.
- **Implementation Guidance**:
    1. Create a system prompt generator in `Application/Agents/AgentPromptComposer.cs`:
       ```markdown
       # Agent Task Harness Operational Protocol
       You are an autonomous agent working on Task ID: {StepId} ("{StepTitle}") of Ticket ID: {FeatureId} ("{FeatureTitle}").
       You have access to the Agent Task Harness MCP tools, including `get_available_actions`.
  
       OPERATIONAL RULES:
       1. Complete all coding, tests, and verifications required by the task within your dedicated git worktree.
       2. When your work is finished, you MUST query the harness MCP tool `get_available_actions` for your current task.
       3. Compare the returned available action names against your assigned task outcome and select the matching action (e.g. SubmitForReview, RequestRework, etc.).
       4. USER CLARIFICATION RULE: If no available action matches what you have been told to do, or if there is any ambiguity about which action to choose, you MUST pause and ask the user in the chat/terminal window:
          "My work on this task is complete. The available workflow transitions are: [action list]. Which action should I invoke?"
       5. Wait for the user's response in the terminal, then invoke the action specified by the user.
       ```
    2. Prepend this system prompt into the agent instructions for every executed task.
- **Acceptance Criteria**:
    - All agent executions have the Harness System Prompt embedded.
    - Agent queries available actions on completion and asks the user in the interactive terminal/chat window if no match is found.

### 6.4 Agent Setup & Execution Context Folder Generation (`.agent-context/`)
- **Problem**: The agent needs an isolated, structured context folder on disk containing its definition file, MCP client settings, task specification, and git guard shim.
- **Implementation Guidance**:
    1. Create `Application/Agents/AgentContextFolderService.cs`:
        - Creates `.agent-context/` inside `step.WorktreePath` (or dedicated execution temp dir).
        - Generates `copilot-instructions.md` containing:
            - YAML frontmatter block (`name`, `description`, `model`, `tools`).
            - Embedded Harness System Prompt.
            - Composed prompt body (with included `AgentComponent` blocks).
        - Generates `TASK_CONTEXT.md` containing:
            - Task Title, Description, Guidance Notes.
            - Parent Ticket Title, Requirements, Acceptance Criteria, Suggested Solution.
        - Generates `mcp_config.json` configuring Copilot CLI to connect to the in-process Harness MCP server.
        - Sets up the Git Guard Shim directory.
    2. Update `CopilotCliProcessRunner`: Pass arguments pointing to the generated context folder.
- **Acceptance Criteria**:
    - Context folder `.agent-context/` is generated automatically before agent process launch.
    - Contains complete frontmatter instruction file, task context, MCP config, and git guard shim.

---

## Phase 7: Global Task Workflow Editor & Workflow Engine

### 7.1 Workflow Data Model & StartedAt Timestamps
- **Problem**: The system needs a user-defined task workflow with custom columns, exactly one entry point, assigned agents, named transitions, and `StartedAt` timestamps on Feature and Step.
- **Implementation Guidance**:
    1. Update `Domain/Entities/Feature.cs`: Add `DateTimeOffset? StartedAt`.
    2. Update `Domain/Entities/Step.cs`: Add `DateTimeOffset? StartedAt`.
    3. Create entities in `Domain/Entities/`:
        - `WorkflowTemplate`: `(Id, Name, Description, CreatedAt)`.
        - `WorkflowStage`: `(Id, WorkflowTemplateId, Name, Order, IsEntryPoint, AssignedAgentDefinitionId)`.
        - `WorkflowTransition`: `(Id, WorkflowTemplateId, FromStageId, ToStageId, ActionName)`.
    4. Update `Board`: Add `WorkflowTemplateId` (foreign key to `WorkflowTemplate`).
    5. Update `Step`: Add `CurrentStageId` (referencing `WorkflowStage`, nullable when in `Not Started`, `Human Review`, or `Done`).
    6. Create EF Core migration `AddTaskWorkflowEngineAndStartedAt`.
- **Acceptance Criteria**:
    - Database supports global workflow templates, stages, transitions, and `StartedAt` tracking.

### 7.2 Global Task Workflow Editor (`/workflows`)
- **Problem**: *"add a task workflow editor. this is the sole place where workflows can be created and edited."*
- **Implementation Guidance**:
    1. Create `Components/Pages/Workflows/WorkflowEditor.razor` at `@page "/workflows"` and `@page "/workflows/{WorkflowId:guid}"`.
    2. Features of the editor:
        - Manage workflow templates (Create, Rename, Delete).
        - Column/Stage manager: Add, reorder, delete custom columns.
        - **Enforce at least one column as the Entry Point** (designated with a distinct badge).
        - **Enforce exactly one agent assigned per column** (dropdown of global `AgentDefinition`s; match criteria is eliminated).
        - Transition manager: Define directed transitions between columns and assign each an **Action Name** (e.g. `SubmitForReview`, `RequestRework`, `Approve`).
- **Acceptance Criteria**:
    - Workflows are created and edited solely at `/workflows`.
    - Every column in the user-defined workflow has exactly one assigned agent.
    - Users can configure transitions with action names between any stages.
    - Workflow cannot be saved without at least one valid Entry Point column.

### 7.3 Ticket vs. Task Workflow Orchestration & Worktree Choice
- **Problem**:
    - Tickets have fixed stages: `Backlog` → `Build` → `Human Review` → `Done`.
    - When a ticket moves to `Build`, set `Feature.StartedAt = DateTimeOffset.UtcNow`, and make child tasks available.
    - Moving a ticket from `Build` back to `Backlog` requires prompting whether to delete or pause worktrees.
    - Tasks have: `Not Started` → `{{User Defined Workflow}}` → `Human Review` → `Done`.
- **Implementation Guidance**:
    1. In `FeatureTransitionOrchestrator.cs`:
        - Restrict Ticket moves to: `Backlog` → `Build` → `Human Review` → `Done`.
        - When moving to `Build`: set `StartedAt = DateTimeOffset.UtcNow`, create Feature Git branch and worktree, then query child tasks whose dependencies are met and make them eligible for the scheduler.
        - When moving from `Build` to `Backlog`:
            - Intercept move in UI (`Board.razor`) and display an interactive modal:
              *"Do you want to discard and delete the Git branch and worktrees for this ticket and all its child tasks, or pause them for later resumption?"*
            - Discard: Delete worktrees and branches via `LibGit2WorktreeService`.
            - Pause: Leave branches intact and mark worktrees paused.
    2. In `StepTransitionOrchestrator.cs`:
        - Handle task movement: `Not Started` → Entry Point → Custom Stages → `Human Review` / `Done`.
        - Set `Step.StartedAt = DateTimeOffset.UtcNow` on initial transition out of `Not Started`.
        - Forward moves to completion check `SkipStepHumanReview`: route through `Human Review` or straight to `Done`.
- **Acceptance Criteria**:
    - Tickets follow the 4-stage lifecycle without running agents.
    - Moving a ticket to Build unlocks its tasks for the scheduler and records `StartedAt`.
    - Moving a ticket back to Backlog prompts with a worktree deletion/pause dialog.
    - Tasks execute across user-defined workflow stages.

### 7.4 MCP Dynamic Workflow Actions
- **Problem**: Agents need to discover which named actions can be performed on the card's current column.
- **Implementation Guidance**:
    1. In `Infrastructure/Mcp/BoardMcpTools.cs`:
        - Update `get_available_actions(cardType, cardId)`:
            - If card is a Task: look up its current `WorkflowStageId` and return the list of `ActionName`s configured on its outgoing transitions.
        - Update action execution tool:
            - Match the requested action name to the corresponding `WorkflowTransition` and execute the stage move.
- **Acceptance Criteria**:
    - Agents querying MCP receive the exact action names configured in the workflow editor for that column.
    - Invoking an action advances the card along the corresponding transition.

### 7.5 Scheduler Prioritization Algorithm: StartedAt, Dependency Count Fallback & Random Tie-Breaker
- **Problem**: Scheduler must select the next task to work on using the specified multi-tiered criteria.
- **Implementation Guidance**:
    1. Update `AgentSchedulerService.cs`:
        - **Column Criteria**: Select tasks in **any column that is NOT in `Human Review` and NOT in `Done`** (i.e. `Not Started` or any user-defined workflow stage).
        - **Agent Running Check**: Must **NOT have an agent currently working on it** (`Status != Working && Status != WaitingForInput`).
        - **Parent Ticket Check**: Parent ticket must be in `Build`.
        - **Dependency Check**: All prerequisites for the task within the ticket must be `Done`.
        - **Failure Threshold Check**: Task has not exceeded review failure threshold.
        - **Multi-tiered Prioritization Logic**:
          ```csharp
          // 1. Group tasks by parent ticket and rank tickets:
          //    a) Tickets with StartedAt != null ordered ascending by StartedAt.
          //    b) If StartedAt is null (or tied), ordered descending by number of dependents (DependedOnBy.Count).
          //    c) If still tied, choose at random (Guid.NewGuid()).
          // 2. Within the top-ranked ticket, rank eligible tasks:
          //    a) Tasks with StartedAt != null ordered ascending by StartedAt.
          //    b) If StartedAt is null (or tied), ordered descending by number of dependents (DependedOnBy.Count).
          //    c) If still tied, choose at random (Guid.NewGuid()).
          ```
        - For each selected task in `Not Started`, transition it to the workflow's Entry Point stage, set `StartedAt = DateTimeOffset.UtcNow`, generate the `.agent-context/` directory, and start the assigned agent.
- **Acceptance Criteria**:
    - Scheduler prioritizes tasks by `StartedAt` first.
    - If `StartedAt` is not available, chooses the item with the highest count of dependencies/dependents.
    - If tied after dependency count, breaks the tie at random.
    - Any task not in `Human Review` or `Done` with met dependencies and no active agent is eligible.
    - Dispatches tasks automatically up to the board's concurrency limit.

---

## Phase 8: Reactive Agent Pickup & Interactive In-App Terminal

### 8.1 Reactive Agent Column Pickup
- **Problem**: *"if a ticket is already in a column before an agent is selected on the column and then an agent is added, then the ticket should get picked up by the agent if the agent criteria is met and the agent is avialable"*.
- **Implementation Guidance**:
    1. In `AgentSchedulerService.cs`:
        - Add `TriggerPickupForStageAsync(Guid boardId, Guid stageId, CancellationToken cancellationToken)`.
        - Query all tasks currently in `stageId` without an active or queued `AgentRun`.
        - Dispatch `RequestStartAsync` for each task up to the board's concurrency limit.
    2. Wire `TriggerPickupForStageAsync` to fire whenever:
        - An agent is assigned to a column.
        - A task enters a column.
        - An active run finishes and frees a concurrency slot.
- **Acceptance Criteria**:
    - Adding or changing an agent on a column with idle tasks immediately triggers the scheduler to start work without manual intervention.

### 8.2 Interactive In-App Terminal (Bi-directional Stdin/Stdout, Tool Approvals & Chat)
- **Problem**: Process runner opens external macOS Terminal.app or outputs `copilot://` links. Users must be able to interact with the terminal within the app (approving tools, answering prompts, chatting with the agent).
- **Implementation Guidance**:
    1. **Process I/O Redirection & Context Injection**:
        - In `CopilotCliProcessRunner.cs`: Launch processes headlessly with redirected `StandardInput`, `StandardOutput`, and `StandardError`.
        - Pass `--config-dir` / instructions pointing to the generated `.agent-context/` folder.
        - Eliminate external AppleScript Terminal.app launch and `copilot://` links.
        - Store process reference or stream writers in an active runner registry (`ActiveProcessRegistry`).
    2. **Bi-directional Terminal Streaming Service (`AgentTerminalSessionService`)**:
        - Output: Streams `stdout` and `stderr` lines/chunks via an event `event Action<Guid, string>? OnOutputReceived`.
        - Input: Method `Task SendInputAsync(Guid runId, string input)` writes text/keystrokes directly to `process.StandardInput.WriteLine(input)` or raw write.
    3. **Web Terminal Component (Xterm.js)**:
        - Add Xterm.js bundle to `wwwroot/` (or via CDN/npm).
        - In `CardPopup.razor`: Embed the terminal component in a dedicated **Terminal** tab.
        - Terminal JSInterop:
            - Mounts interactive terminal emulator in the popup.
            - Subscribes to backend output events and calls `term.write(data)`.
            - Listens to terminal keystrokes (`term.onData(...)`) and invokes C# `SendInputAsync`.
    4. **Tool Approval & Chat Window**:
        - Tool approval prompts (`[y/n/a]`) emitted by the CLI appear directly in the terminal, and users can approve or reject them directly via keyboard input.
        - Users can type chat messages directly into the terminal prompt to converse with the agent.
- **Acceptance Criteria**:
    - Zero external OS terminal windows open.
    - Zero `copilot://` links appear.
    - Users can view live streaming ANSI output inside the web app.
    - Users can interactively type into the terminal, approve tool runs, and chat with the running agent.

---

## 9. Verification & Testing Plan

1. **Unit & Integration Tests**:
    - `AgentContextFolderServiceTests`: Verify generation of `.agent-context/` directory with frontmatter YAML `copilot-instructions.md`, `TASK_CONTEXT.md`, `mcp_config.json`, and git shim.
    - `SchedulerPrioritizationTests`: Verify sorting:
        - Oldest ticket by `StartedAt`, then oldest task by `StartedAt`.
        - Fallback: when `StartedAt` is null, sort descending by dependency count (`DependedOnBy.Count`).
        - Tie-breaker: random selection when dependency counts tie.
        - Verify filtering tasks in any column not in `HumanReview`/`Done` and having no active agent.
    - `AgentPromptComposerTests`: Verify embedded harness system prompt generation and MCP instructions.
    - `FeatureDependencyServiceTests` & `StepDependencyServiceTests`: Verify bidirectional link creation, bidirectional deletion, and cycle prevention.
    - `WorkflowEngineTests`: Verify 1 Entry Point invariant, named transition routing, and Human Review skipping.
    - `AgentTerminalSessionTests`: Verify bi-directional I/O pipe between Xterm.js and `process.StandardInput`/`process.StandardOutput`.
2. **End-to-End User Journeys**:
    - **Agent Setup**: Create agent definition, move task to execution, inspect generated `.agent-context/` directory on disk.
    - **Interactive Terminal**: Launch a task agent, observe real-time output in `CardPopup`'s terminal tab, send tool approval (`y`), and verify command execution.
    - **System Prompt Protocol**: Agent completes task, calls `get_available_actions`, requests user clarification in terminal if ambiguous, and advances task.
    - **Scheduler Dispatch**: Place multiple tickets and tasks in backlog, move tickets to Build, verify the oldest task of the oldest ticket by `StartedAt` starts first; verify fallback to dependency count and random tie-breaker.
    - **Navigation & UI**: Verify clean top-right header, `/workflows` editor, `<AppButton>` styling, and drag-and-drop emerald/rose outlines.
