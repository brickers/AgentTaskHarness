# Agent Task Harness - Issues 2 Comprehensive Implementation Plan

This implementation plan translates all requirements from `issues2.md` into an actionable, phased engineering blueprint with technical guidance, architecture specifications, database changes, UI/UX designs, and detailed acceptance criteria for every item.

---

## Executive Summary & Requirement Matrix

| # | Requirement from `issues2.md` | Target Area | Implementation Phase |
| :--- | :--- | :--- | :--- |
| **1** | Edit both sides of dependencies with similar view | Backend & UI | **Phase 3: Universal Dependencies** |
| **2** | Useful messages visible as toast at bottom of screen | UI Infrastructure | **Phase 2: Global Toast System** |
| **3** | No notification when task/ticket moved between columns | UI & Orchestrators | **Phase 2: Global Toast System** |
| **4** | Set dependencies when creating a ticket or task | UI Creation Modals | **Phase 3: Universal Dependencies** |
| **5** | Single status in popup; status legend on board (closed by default) | UI Components | **Phase 4: Popup & Board Polish** |
| **6** | Tickets editable in popup unless agent actively working | UI & Application | **Phase 4: Popup & Board Polish** |
| **7** | Tasks have dependencies matching tickets logic & components | Domain, App, UI | **Phase 3: Universal Dependencies** |
| **8** | Reactive agent pickup when agent added to column with existing tickets | Scheduler Worker | **Phase 5: Agent Reactive Pickup** |
| **9** | Remove "Agent Task Harness" from top right | Layout & Navbar | **Phase 1: Architecture & Tailwind** |
| **10** | Agents defined at top level globally, not within a board | Domain, EF, App | **Phase 1: Architecture & Tailwind** |
| **11** | Top menu: Boards & Agents; Board subchoice: Roadmap & Board | Layout & Routing | **Phase 1: Architecture & Tailwind** |
| **12** | Consistent breadcrumbs separate from header showing board/agent | Navigation Layout | **Phase 1: Architecture & Tailwind** |
| **13** | Consistent page headings and title bar across all pages | Layout Components | **Phase 1: Architecture & Tailwind** |
| **14** | Board settings removed from board view; dedicated settings menu | Board UI & Routing | **Phase 1: Architecture & Tailwind** |
| **15** | Consistent links and buttons across entire app | Design System | **Phase 1: Architecture & Tailwind** |
| **16** | Use Tailwind CSS default look and feel; eliminate custom CSS | Styling Engine | **Phase 1: Architecture & Tailwind** |
| **17** | Custom workflow engine with restrictions (Backlog, Ready, 1 Entry Point, HR, Done) | Domain, DB, Workflow | **Phase 6: Custom Workflow Engine** |
| **18** | In-app terminal display instead of external window / deep links | Process Runner & UI | **Phase 5: Agent In-App Terminal** |

---

## Phase 1: Information Architecture, Navigation, Settings & Tailwind Styling

### 1.1 Global Agent Definitions (Decoupled from Boards)
- **Current Problem**: `AgentDefinition` has a required `BoardId` foreign key and is managed at `/boards/{BoardId}/agents`.
- **Implementation Guidance**:
    1. Update `Domain/Entities/AgentDefinition.cs`:
        - Remove `BoardId` and `Board` navigation property (or make `BoardId` nullable `Guid?` during migration transition). Agents are globally accessible building blocks.
        - Agents are linked to specific boards only via `AgentColumnAssignment(BoardId, ColumnScope/StageId, AgentDefinitionId)`.
    2. Create an EF Core migration `MakeAgentDefinitionsGlobal`.
    3. Update `AgentDefinitionService.cs`:
        - Replace `GetForBoardAsync(Guid boardId)` with `GetAllAsync()`.
        - Update `CreateAsync(...)` to no longer require a `BoardId`.
    4. Move `AgentDefinitionEditor.razor` to top-level route `@page "/agents"` (and `@page "/agents/{AgentId:guid}"`).
- **Acceptance Criteria**:
    - Agent definitions can be created, updated, and deleted globally at `/agents` without needing an active board.
    - Global agent definitions appear in column assignment selectors across all boards.

### 1.2 Top-Level Menu & Sub-Navigation Structure
- **Current Problem**: Navbar mixes boards, current board tabs, and agents inconsistently. "Agent Task Harness" branding appears redundantly.
- **Implementation Guidance**:
    1. In `MainLayout.razor` & `NavMenu.razor`:
        - **Top Level Navbar**:
            - Left brand mark: minimal icon/wordmark linking to `/boards`.
            - Top-level links: **Boards** (`/boards`) and **Agents** (`/agents`).
            - Remove any branding / text from the top right. Top right reserved for global status or left completely clean.
        - **Contextual Board Subnavigation Bar**:
            - When viewing a board route (`/boards/{BoardId}/...`), display a secondary horizontal sub-nav:
                - **Board** (`/boards/{BoardId}`)
                - **Roadmap** (`/boards/{BoardId}/roadmap`)
                - **Settings** (`/boards/{BoardId}/settings`)
- **Acceptance Criteria**:
    - The top menu contains only **Boards** and **Agents** at root level.
    - When inside a board context, the sub-nav displays **Board**, **Roadmap**, and **Settings**.
    - No "Agent Task Harness" title appears in the top right.

### 1.3 Universal Breadcrumb Component
- **Current Problem**: Breadcrumbs are embedded haphazardly inside page titles and headers.
- **Implementation Guidance**:
    1. Create `Components/Shared/AppBreadcrumb.razor` placed at the top of `@Body` in `MainLayout.razor` (or embedded directly above page content):
        - Renders below the navigation header, above page content.
        - Automatically parses route segments or accepts parameters:
            - `/boards`: `Boards`
            - `/boards/{id}`: `Boards / [Board Name] / Board`
            - `/boards/{id}/roadmap`: `Boards / [Board Name] / Roadmap`
            - `/boards/{id}/settings`: `Boards / [Board Name] / Settings`
            - `/agents`: `Agents`
            - `/agents/{id}`: `Agents / [Agent Name]`
- **Acceptance Criteria**:
    - Breadcrumb bar is visually distinct, rendered consistently at the top of every page.
    - Displays clickable parent links and current item name.

### 1.4 Dedicated Board Settings Page
- **Current Problem**: Board settings are summarized in tags on the board header and edited via an inline modal, cluttering the Kanban view.
- **Implementation Guidance**:
    1. Create `Components/Pages/Boards/BoardSettings.razor` at route `@page "/boards/{BoardId:guid}/settings"`.
    2. Move board configuration forms (Name, Repo Path, Concurrency Limit, Review Skips, Failure Thresholds, and Stage Assignments) to this page.
    3. Remove settings summary tags and modal editor from `Board.razor`.
- **Acceptance Criteria**:
    - The main board view contains no settings summary tags or settings edit modal.
    - Board settings are configured exclusively on the dedicated `/boards/{id}/settings` page.

### 1.5 Tailwind CSS Design System & Button/Link Consistency
- **Current Problem**: Mismatched styles (`.button-primary`, `.btn-primary`, `.button-quiet`, inline `<style>` tags). Custom CSS lacks unity.
- **Implementation Guidance**:
    1. Add Tailwind CSS CDN / standalone CLI to `App.razor`.
    2. Standardize button & link utility styles:
        - Primary Button: `inline-flex items-center px-4 py-2 bg-indigo-600 hover:bg-indigo-700 text-white font-medium text-sm rounded-md shadow-sm transition`
        - Secondary / Outline Button: `inline-flex items-center px-4 py-2 bg-white hover:bg-gray-50 text-gray-700 font-medium text-sm border-[3px] border-gray-300 rounded-md shadow-sm transition`
        - Danger Button: `inline-flex items-center px-4 py-2 bg-red-600 hover:bg-red-700 text-white font-medium text-sm rounded-md shadow-sm transition`
        - Header Title Bar: `mb-6 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 pb-4 border-b-[3px] border-gray-200`
    3. Eliminate custom CSS files where Tailwind classes provide identical presentation.
- **Acceptance Criteria**:
    - Buttons, links, forms, and headers across Board, Roadmap, Agents, and Settings share identical Tailwind typography, spacing, and hover states.

---

## Phase 2: Global Toast Notification System

### 2.1 Toast Service & Container
- **Current Problem**: Alerts are scattered across inline banners, top-right absolute divs, and modal alerts. Drag-and-drop column moves trigger unnecessary notifications.
- **Implementation Guidance**:
    1. Create `Application/Notifications/IToastService.cs` and `ToastService.cs`:
        - Methods: `ShowSuccess(string message)`, `ShowError(string message)`, `ShowInfo(string message)`.
        - Maintains a thread-safe list of active toast items with auto-dismiss timers (default 4 seconds).
        - Event `event Action? OnChanged`.
    2. Register `IToastService` as Scoped in `Program.cs`.
    3. Create `Components/Shared/ToastContainer.razor` in `MainLayout.razor`:
        - Position: `fixed bottom-6 right-6 z-50 flex flex-col gap-2 max-w-md w-full pointer-events-none`
        - Individual toast: `pointer-events-auto rounded-lg p-4 shadow-lg flex items-center justify-between text-sm transition-all`
            - Success: `bg-emerald-50 text-emerald-900 border-[3px] border-emerald-200`
            - Error: `bg-rose-50 text-rose-900 border-[3px] border-rose-200`
            - Info: `bg-sky-50 text-sky-900 border-[3px] border-sky-200`
    4. Audit & Replace:
        - Replace inline `_notificationMessage` in `Board.razor`, `BoardList.razor`, `CardPopup.razor`, and `AgentDefinitionEditor.razor` with `ToastService`.
        - **Remove** notifications when dragging or moving cards between columns (`HandleFeatureDrop`, `HandleStepDrop`). Moving cards between columns updates the board state silently.
- **Acceptance Criteria**:
    - All actionable messages (e.g. "Step created", "Feature created", "Board saved", errors) display as toasts at the bottom of the screen.
    - Moving a card/step between columns produces **zero** notification toasts.
    - Toasts auto-dismiss after timeout and provide a manual close button.

---

## Phase 3: Universal Dependencies (Bidirectional & Step Integration)

### 3.1 Bidirectional Dependency Service Methods
- **Current Problem**: Dependencies can only be viewed and added from the perspective of the dependent item ("Depends On"). Blockers ("Depended On By") cannot be managed or viewed symmetrically.
- **Implementation Guidance**:
    1. `FeatureDependencyService.cs`:
        - Add `GetDependentsAsync(Guid featureId)`: Returns features that depend on `featureId` (`Where d.DependsOnFeatureId == featureId`).
        - Add `AddDependentAsync(Guid featureId, Guid dependentFeatureId)`: Semantically equivalent to `AddAsync(dependentFeatureId, featureId)`.
        - Add `RemoveDependentAsync(Guid featureId, Guid dependentFeatureId)`.
        - Ensure cycle prevention checks pass in both directions.
    2. `StepDependencyService.cs`:
        - Add matching methods: `GetDependentsAsync(Guid stepId)`, `AddDependentAsync(Guid stepId, Guid dependentStepId)`, `RemoveDependentAsync`.
- **Acceptance Criteria**:
    - Given Feature A and Feature B, adding B as a dependent of A creates the exact same relationship as opening B and adding A as a prerequisite.
    - Cycle detection blocks invalid relationships regardless of which end initiates the link.

### 3.2 Symmetrical Dependency Editor Component
- **Implementation Guidance**:
    1. Create `Components/Shared/CardDependencySection.razor`:
        - Renders two matching side-by-side or stacked panels:
            - **Prerequisites (Depends On / Blocked By)**: Cards that must complete before this card can proceed.
            - **Dependents (Depended On By / Blocking)**: Cards waiting on this card to complete.
        - Each side shows:
            - List of items with title, column badge, and remove (`×`) button.
            - Dropdown selector of candidate cards with an "Add" button.
        - Gated by card editable state (e.g. only enabled when in Backlog or before agent start).
- **Acceptance Criteria**:
    - The UI for prerequisites and dependents uses identical styling, actions, and status badges.
    - Users can add/remove links from either card.

### 3.3 Set Dependencies on Creation
- **Current Problem**: Modals for creating Features and Steps only accept text properties; dependencies can only be added post-creation.
- **Implementation Guidance**:
    1. In `Board.razor` (and `Roadmap.razor`) "Create Feature" modal:
        - Add a "Prerequisite Dependencies" multi-select dropdown populated with existing Backlog/Ready features.
        - On submission: create the Feature, then immediately call `FeatureDependencyService.AddAsync` for each selected prerequisite.
    2. In "Add Step" modal:
        - Add a "Prerequisite Steps" multi-select dropdown populated with existing steps of the parent feature.
        - On submission: create the Step, then call `StepDependencyService.AddAsync` for each selected prerequisite step.
- **Acceptance Criteria**:
    - Creating a Feature allows choosing existing features as dependencies directly in the creation modal.
    - Creating a Step allows choosing existing sibling steps as dependencies directly in the creation modal.
    - Newly created items appear with dependencies already attached.

### 3.4 Tasks (Steps) Full Dependency Parity
- **Current Problem**: `CardPopup.razor` hides the Dependencies tab for Steps (`@if (IsFeature)`).
- **Implementation Guidance**:
    1. Enable `<CardDependencySection />` inside `CardPopup.razor` for BOTH Features and Steps.
    2. Pass Step dependencies and candidates scoped to the parent Feature.
- **Acceptance Criteria**:
    - Opening any Step in `CardPopup` displays the full bidirectional Dependencies tab, matching Feature cards.

---

## Phase 4: Popup & Board Polish

### 4.1 Single Status Display & Board Status Legend
- **Current Problem**: `CardPopup` displays status in 3 different sections plus an embedded status legend.
- **Implementation Guidance**:
    1. `CardPopup.razor`:
        - Keep a single status badge in the header: `<StatusIcon Status="@CurrentRun?.Status" ShowText="true"/>`.
        - Remove the `status-explanation-card` and remove the status legend section from the popup entirely.
    2. `Board.razor`:
        - Add a collapsible status legend component (`<StatusLegend />`) placed in a compact floating drawer or bottom-right corner of the board.
        - Default state: collapsed (`_isOpen = false`).
        - Clicking expands the legend to display all status icons and explanations.
- **Acceptance Criteria**:
    - Status is displayed in only one place in `CardPopup`.
    - The status legend is completely absent from `CardPopup`.
    - The status legend lives on the Kanban board and defaults to closed/collapsed.

### 4.2 In-Popup Card Editing (Locked When Agent Active)
- **Current Problem**: Details inside `CardPopup` are read-only text divs.
- **Implementation Guidance**:
    1. In `CardPopup.razor`:
        - Render form inputs (Title, Requirements, Acceptance Criteria, Suggested Solution for Features; Title, Description, Guidance Notes for Steps).
        - Provide a "Save Changes" button.
        - If `IsAgentRunning` is `true`:
            - Disable all inputs (`disabled="@IsAgentRunning"`).
            - Display a warning badge: *"Editing is locked while an agent is actively running on this card."*
        - If `IsAgentRunning` is `false`:
            - Inputs are enabled and saving persists updates to the database via `FeatureService.UpdateAsync` or `StepService.UpdateAsync`.
- **Acceptance Criteria**:
    - Card details can be edited and saved directly from inside the popup when the card is idle.
    - When an agent is working or waiting for input, all fields are locked and disabled.

---

## Phase 5: Agent Reactive Column Pickup & In-App Terminal

### 5.1 Reactive Agent Pickup on Column Assignment
- **Current Problem**: When an agent is assigned to a column in `Board.razor`, existing tickets sitting in that column are not picked up until a manual event occurs.
- **Implementation Guidance**:
    1. In `AgentMatchingService` / `AgentSchedulerService`:
        - Add `TriggerQueueForColumnAsync(Guid boardId, ColumnScope scope, CancellationToken cancellationToken)`.
        - Logic:
            1. Identify all cards residing in the corresponding column (e.g. Steps in Build, Steps in AgentReview, Features in AgentReview).
            2. Filter to cards not currently running an agent.
            3. Ensure a `Queued` `AgentRun` exists for each eligible card.
            4. Call `ProcessQueueAsync(boardId)` to immediately start agents up to the concurrency limit.
    2. In `Board.razor` (and `BoardSettings.razor`):
        - Trigger `TriggerQueueForColumnAsync` whenever an agent definition is assigned or changed for a column.
- **Acceptance Criteria**:
    - If cards are already resting in a column and an agent is subsequently assigned to that column, the agent immediately starts work on the cards without manual user intervention.

### 5.2 Embedded In-App Terminal (Replacing External Windows & `copilot://` Links)
- **Current Problem**: `CopilotCliProcessRunner` opens macOS Terminal.app via AppleScript or emits `copilot://session` links that launch outside the browser.
- **Implementation Guidance**:
    1. `Infrastructure/Agents/AgentTerminalHub.cs` or `AgentOutputBufferService.cs`:
        - Singleton service maintaining thread-safe circular text buffers (e.g. 1000 lines) keyed by `AgentRun.Id` / `Step.Id`.
        - Event `event Action<Guid, string>? OnOutputReceived`.
    2. In `CopilotCliProcessRunner.cs`:
        - Remove AppleScript / Terminal.app launching.
        - Always launch CLI processes headlessly with `RedirectStandardOutput = true` and `RedirectStandardError = true`.
        - Attach asynchronous data readers (`OutputDataReceived`, `ErrorDataReceived`) and push output chunks to `AgentOutputBufferService`.
    3. In `CardPopup.razor`:
        - Replace the "Open Agent Window" link with a "Terminal" tab / drawer.
        - Render an embedded console viewer:
            - Dark monospace terminal panel (`bg-gray-950 text-emerald-400 font-mono text-xs p-4 rounded-lg h-96 overflow-y-auto`).
            - Displays real-time streaming output of the running agent.
            - Includes "Clear", "Auto-scroll", and "Copy" actions.
- **Acceptance Criteria**:
    - No external terminal windows (Terminal.app) are opened.
    - No external `copilot://` links are used.
    - Agent terminal logs stream live inside the application UI.

---

## Phase 6: Custom Workflow Engine with Structural Restrictions

### 6.1 Workflow Engine Architecture & Restrictions
- **Requirement from `issues2.md`**:
    - Backlog - not ready to work
    - Ready - ready for scheduler to pick up, move to build and assign to an agent
    - Human review - waiting for human review. skipped if configured
    - Done - fully complete
    - Workflow must define **exactly one entry point**: scheduler moves tickets from Ready to this stage.
    - Workflows can define **multiple transitions to 'Done'**: the app enforces whether transitions route direct to Done or require Human Review first.

### 6.2 Data Model & Schema
1. Create `Domain/Entities/WorkflowStage.cs`:
   ```csharp
   public enum StageType
   {
       Backlog,
       Ready,
       EntryPoint,    // Exactly one per board
       Custom,        // Intermediate stages (e.g. QA, Review, Lint)
       HumanReview,
       Done           // Terminal
   }

   public class WorkflowStage
   {
       public Guid Id { get; set; } = Guid.NewGuid();
       public Guid BoardId { get; set; }
       public string Name { get; set; } = string.Empty;
       public StageType Type { get; set; }
       public int Order { get; set; }
       public Board Board { get; set; } = null!;
       public ICollection<WorkflowTransition> OutgoingTransitions { get; set; } = new List<WorkflowTransition>();
       public ICollection<WorkflowTransition> IncomingTransitions { get; set; } = new List<WorkflowTransition>();
   }

   public class WorkflowTransition
   {
       public Guid FromStageId { get; set; }
       public Guid ToStageId { get; set; }
       public WorkflowStage FromStage { get; set; } = null!;
       public WorkflowStage ToStage { get; set; } = null!;
   }
   ```
2. Update `Feature` and `Step`:
    - Replace or map `WorkflowColumn` to `Guid CurrentStageId` referencing `WorkflowStage`.
3. Migration:
    - Seed default 6 stages (Backlog, Ready, Build [EntryPoint], AgentReview [Custom], HumanReview, Done) for existing boards.

### 6.3 Lifecycle Invariant Enforcer
- Implement `WorkflowEngineService`:
    1. **Validation Rules**:
        - Exactly one stage with `Type == StageType.Backlog`. Cards are created here.
        - Exactly one stage with `Type == StageType.Ready`. Transition allowed only when dependencies complete.
        - Exactly one stage with `Type == StageType.EntryPoint`. Scheduler automatically advances cards from `Ready` to this stage and starts the assigned agent.
        - Zero or more `Custom` stages.
        - Exactly one `HumanReview` stage.
        - Exactly one `Done` stage (terminal).
    2. **Enforcing Multiple Transitions to 'Done'**:
        - When any stage transitions to `Done`:
            - The engine evaluates board settings (`SkipFeatureHumanReview` / `SkipStepHumanReview`) and card override (`AlwaysRequireHumanReview`).
            - If human review is required, the move is intercepted and routed into `HumanReview`.
            - If human review is skipped, the move completes directly into `Done`.
    3. **Scheduler Integration**:
        - The scheduler queries cards in `Ready` whose dependencies are satisfied, automatically transitions them to the board's designated `EntryPoint` stage, and executes the assigned agent.

### 6.4 Workflow Configuration UI
- Located in `BoardSettings.razor` under a "Workflow Configuration" tab:
    - Visual stage manager: add, remove, rename custom stages, and reorder stages.
    - Stage type assignment (enforcing that exactly one Entry Point exists).
    - Matrix / toggle list of allowed transitions between stages.
- **Acceptance Criteria**:
    - Boards can configure custom intermediate workflow stages and custom transition paths.
    - System strictly enforces Backlog, Ready, 1 Entry Point, Human Review gating, and Done invariants.
    - Scheduler automatically moves dependency-ready cards from Ready to the designated Entry Point.
    - Transitions to Done automatically honor the Human Review skip/require configuration.

---

## Testing & Verification Plan

1. **Unit & Integration Tests**:
    - `FeatureDependencyServiceTests` & `StepDependencyServiceTests`: Verify bidirectional link creation, bidirectional deletion, and cycle detection.
    - `WorkflowEngineTests`: Validate stage invariants (exactly 1 Entry Point, Human Review routing logic, terminal Done behavior).
    - `AgentSchedulerTests`: Verify immediate reactive pickup when an agent is assigned to a column containing idle tickets.
2. **End-to-End Verification**:
    - Navigate top-level `/boards` and `/agents`. Verify breadcrumb display and title bar uniformity.
    - Open card popup: verify single status indicator, form editing, and terminal output tab.
    - Create a feature and step with pre-selected dependencies; confirm links appear immediately.
    - Verify drag-and-drop between columns succeeds with no toast alerts, while ticket creation produces a bottom-screen toast.
