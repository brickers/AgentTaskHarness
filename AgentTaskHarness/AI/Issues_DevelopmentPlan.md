# Agent Task Harness - Development Action Plan & Implementation Status

This plan breaks down the 15 issues from `Issues.md` into logical, manageable phases. By following this sequential approach, development iterates from quick UI wins to complex backend flows and system integrations.

---

## Executive Summary & Progress Overview

| Phase | Item | Status | Key Implementation Notes |
| :--- | :--- | :---: | :--- |
| **Phase 1: UX/UI Polish & Quick Wins** | 1. Fix Checkbox Alignment | **Completed** | Flexbox vertical centering added in `app.css`. |
| | 2. Consistent Menu Navigation | **Completed** | Unified `NavMenu.razor` embedded into `MainLayout.razor`. |
| | 3. Status Indicator Explanations | **Completed** | Tooltips in `StatusIcon.razor` & legend in `CardPopup.razor`. |
| | 4. Separate Row Click vs. Edit (Features) | **Completed** | Row click filters steps; explicit `✏️` button opens popup. |
| | 5. Clear Feature Filter (Unselect) | **Completed** | `✕ Clear Filter` button and unselect toggle on `Board.razor`. |
| **Phase 2: Data Model & Agent Management** | 1. Agents in Database, not Hard Drive | **Completed** | Prompt, instructions, and tool configuration are persisted on `AgentDefinition`; filesystem persistence is legacy-only. |
| | 2. Editable Agent Components | **Completed** | Components can be created and edited in place from `AgentDefinitionEditor.razor`. |
| | 3. Move Agent Assignment to Column View | **Completed** | Agent selectors are available in Build and Agent Review column headers on `Board.razor`. |
| **Phase 3: Validation, Paths & Drag-and-Drop** | 1. Fix Browser Folder Selection | **Completed** | Removed the browser-only folder picker; users enter the server-visible repository path directly. |
| | 2. Git Repository Validation | **Completed** | Board creation and updates reject paths that are not existing valid Git repositories. |
| | 3. Kanban Drag and Drop | **Completed** | HTML5 drag-and-drop with valid/invalid destination highlights. |
| **Phase 4: Roadmap & Dependencies** | 1. Create Features on Roadmap | **Not Completed** | No "Create Feature" button or form on `Roadmap.razor`. |
| | 2. Feature Dependencies (Backend & UI) | **Partially Completed** | Backend & Roadmap UI done; missing on `CardPopup.razor`. |
| | 3. Dependency Graph/Visualization | **Partially Completed** | `FeatureGraphLayout.cs` drafted, but not wired into `Roadmap.razor`. |
| **Phase 5: Agent Automation & OS Integrations** | 1. Workflow Pipeline Triggers | **Partially Completed** | Moving Steps to Build queues agents; Feature to Build does not auto-advance Steps. |
| | 2. OS Copilot Integration & Window Links | **Not Completed** | Registered as `NoOpAgentProcessRunner`; no macOS/AppleScript trigger or window links. |

---

## Phase 1: UX/UI Polish & Quick Wins (Completed)

*These tasks improve immediate usability without requiring architectural changes.*

1. **Fix Checkbox Alignment**
    - **Status**: **Completed**
    - **Target**: `wwwroot/app.css` & related Razor components.
    - **Action**: Fix CSS alignment for checkboxes so tick boxes and their labels align horizontally (e.g., using `display: flex; align-items: center;`).
    - **Implementation Details**: Added flexbox alignment and centered labels for `.checkbox-label`, `.form-check`, and `.form-check-input` in `AgentTaskHarness/wwwroot/app.css`.

2. **Consistent Menu Navigation**
    - **Status**: **Completed**
    - **Target**: `MainLayout.razor` / `NavMenu.razor`.
    - **Action**: Unify navigation across the entire app so all pages have access to the same sidebar/header.
    - **Implementation Details**: Built `AgentTaskHarness/Components/Layout/NavMenu.razor` and wired it into `MainLayout.razor`. Provides direct contextual navigation to **All Boards**, **Board**, **Roadmap**, and **Agents**, displaying the active board badge dynamically.

3. **Status Indicator Explanations**
    - **Status**: **Completed**
    - **Target**: Task/Ticket popup viewer components.
    - **Action**: Add tooltips (`title` attributes or Blazor tooltip components) and a legend in the popup view detailing what each status indicator means.
    - **Implementation Details**: Added rich descriptive tooltips to `StatusIcon.razor` (`TooltipText` and `GetStatusDescription`). Added an interactive, collapsible **Status Indicator Legend** inside `CardPopup.razor`.

4. **Separate Row Clicking vs. Editing (Features)**
    - **Status**: **Completed**
    - **Target**: Kanban/Board column component.
    - **Action**: Remove the `onclick` handler on the entire feature row that opens the popup. Assign the filtering logic to the row click, and create an explicit "Edit" button icon that explicitly opens the popup.
    - **Implementation Details**: Updated `FeatureCard.razor`. Clicking the card item fires `OnSelect` to filter child steps on the board. An explicit `✏️` button with `@onclick:stopPropagation="true"` triggers `OnOpenDetails` to launch the modal popup.

5. **Clear Feature Filter (Unselect)**
    - **Status**: **Completed**
    - **Target**: Kanban/Board view component.
    - **Action**: When a feature is clicked/filtered, display a clear "X" or "Clear Filter" button at the top of the board to unselect the feature and reveal all steps again.
    - **Implementation Details**: Updated `Board.razor`. Added a `✕ Clear Filter` button in the dev plan steps header when a feature is selected. Re-clicking an active feature also toggles off the selection.

---

## Phase 2: Data Model & Agent Management (Completed)

*Focuses on fixing database models and editing configurations.*

1. **Agents in Database, not Hard Drive**
    - **Status**: **Completed**
    - **Target**: `Domain/Entities/AgentDefinition.cs`, `Infrastructure/Persistence/AppDbContext.cs`.
    - **Action**: Remove the directory/folder requirement from the agent definition model. Migrate all defining configurations (prompts, tools) to properties on the Entity. Add EF Core migration for the database schema update.
    - **Implementation**: Added database-backed `Prompt`, `Instructions`, and `ToolConfiguration` fields, removed `FolderPath` from EF mapping, added `StoreAgentConfiguration` migration, and updated the service and CLI runner to consume the stored configuration. A `[NotMapped]` obsolete compatibility property and legacy overload remain for existing integrations.

2. **Editable Agent Components**
    - **Status**: **Completed**
    - **Target**: Agent components (`Components/Pages/Boards/AgentDefinitionEditor.razor`).
    - **Action**: Add forms mapped to the database-driven `AgentDefinition` to allow users to create, update, and manage agents visually.
    - **Implementation**: Added in-place component edit modal backed by `UpdateComponentAsync`, and replaced folder-path fields with prompt, instructions, and tool configuration fields.

3. **Move Agent Assignment to Column View**
    - **Status**: **Completed**
    - **Target**: Column Configuration & Agent Views.
    - **Action**: Assign agents from the Kanban column settings rather than requiring the agents view.
    - **Implementation**: Added database-backed agent selectors to Build and Agent Review column headers for feature and step scopes. Changes create, update, or remove `AgentColumnAssignment` records through `AgentMatchingService`.

---

## Phase 3: Validation, Paths & Board Drag-and-Drop (Completed)

*Improves data validation and core board interactions.*

1. **Fix Browser Folder Selection (Board Creation)**
    - **Status**: **Completed**
    - **Target**: Board creation component (`Components/Pages/Boards/BoardList.razor`).
    - **Action**: Web browsers restrict retrieving absolute paths for security (throwing standard support errors). If a true path is needed on the backend, replace the `<input type="file" webkitdirectory />` with a direct `<input type="text" />` letting the user paste the path manually, or build a server-side tree browser.
    - **Current State**: The repository path is entered through a direct text `<input id="new-repository-path" @bind="_newRepoPath" />`. The browser-only `FolderPicker` control is no longer rendered.
    - **Implementation**: Updated the help text to explain that the path must be visible to the server.

2. **Git Repository Validation**
    - **Status**: **Completed**
    - **Target**: Board creation logic (`Application/Boards/BoardService.cs`).
    - **Action**: Before saving the board, use `LibGit2Sharp` to validate if the given path contains a valid `.git` repository (e.g., `Repository.IsValid(path)`). If false, add a validation error message in the UI preventing creation.
    - **Implementation**: `BoardService.Validate` now requires an existing directory containing a valid Git repository via `LibGit2Sharp.Repository.IsValid(repoPath)`. The same validation runs for both `CreateAsync` and `UpdateAsync`, and failures return the descriptive message *"The specified path does not contain a valid Git repository."* to the UI.

3. **Kanban Drag and Drop**
    - **Status**: **Completed**
    - **Target**: Board/Column/Task UI components (`Board.razor`, `FeatureCard.razor`, `StepCard.razor`).
    - **Action**: Implement drag and drop (using HTML5 native drag events or a library). Add CSS classes to highlight valid destination columns when a card is dragged over them, and visually block invalid ones based on state transition rules.
    - **Implementation Details**: Implemented using HTML5 drag-and-drop. On drag start, `FeatureOrchestrator.GetAllowedMovesAsync()` or `StepOrchestrator.GetAllowedMovesAsync()` calculates valid target columns. Dynamically applies `.drop-target-valid`, `.drop-target-invalid`, and `.drop-over-active` CSS styles in `Board.razor.css`. Drops execute transitions via the orchestrators.

---

## Phase 4: Roadmap & Dependencies (Partially Completed)

*Adding higher-level project management features.*

1. **Create Features on Roadmap**
    - **Status**: **Not Completed**
    - **Target**: Roadmap UI (`Components/Pages/Roadmap/Roadmap.razor`).
    - **Action**: Add a "Create Feature" button and form accessible directly from the Roadmap timeline view.
    - **Current State**: Features can currently only be created from the Kanban board (`Board.razor`). `Roadmap.razor` only has navigation links to the board and agents.
    - **Remaining Work**:
        - Add a "+ New Feature" button to the header in `Roadmap.razor`.
        - Add feature creation modal dialog with title, requirements, acceptance criteria, and solution notes.

2. **Feature Dependencies (Backend & UI)**
    - **Status**: **Partially Completed**
    - **Target**: `Domain/Entities/Feature.cs`, `Application/Features/FeatureDependencyService.cs`, `Components/Shared/CardPopup.razor`.
    - **Action**: Add a many-to-many self-referencing relationship (e.g., `DependsOn`, `RequiredBy`). Add UI on the feature details popup to select dependencies.
    - **Current State**:
        - **Backend**: Fully implemented. `FeatureDependency` entity with EF Core self-referencing navigation, cycle detection, and backlog validation in `FeatureDependencyService`.
        - **Roadmap UI**: Features list their dependencies and blockers; Backlog features allow adding and removing dependencies.
        - **Popup UI (Missing)**: `CardPopup.razor` has no dependency management controls.
    - **Remaining Work**:
        - Add a "Dependencies" tab/section in `CardPopup.razor` allowing users to view and edit dependencies when inspecting a feature.

3. **Dependency Graph/Visualization**
    - **Status**: **Partially Completed / In Progress**
    - **Target**: Roadmap viewer (`Roadmap.razor`).
    - **Action**: Introduce a visual graph representation for feature dependencies (consider Mermaid.js via JSInterop) and list dependencies explicitly in the detailed view of both the dependent and dependee.
    - **Current State**: An untracked layout helper `Components/Pages/Roadmap/FeatureGraphLayout.cs` exists which computes topological ranks, node positions, and SVG bezier connecting paths. However, `Roadmap.razor` currently only renders a responsive card grid and does not render the SVG/DAG graph.
    - **Remaining Work**:
        - Render the visual DAG/SVG diagram in `Roadmap.razor` using `FeatureGraphLayout` or Mermaid.js.
        - Provide toggle between Graph View and Card/List View.

---

## Phase 5: Agent Automation & OS Integrations (Incomplete)

*Completing the closed loop with the physical OS agents.*

1. **Workflow Pipeline Triggers**
    - **Status**: **Partially Completed**
    - **Target**: Stage transition services (`StepTransitionOrchestrator.cs`, `FeatureTransitionOrchestrator.cs`).
    - **Action**: Wire up the logic so that moving a feature and its steps into 'Ready' -> 'Build' triggers the assigned agent automatically in the backend worker process. The UI should reflect the status change visually (e.g., "Queued" -> "Agent Working...").
    - **Current State**:
        - Moving a Step into `Build` or `AgentReview` triggers `AgentScheduler.RequestStartAsync`, and moving the first step into Build auto-advances the Feature from `Ready` to `Build`. The UI displays status dots ("Queued", "Working").
        - Moving a Feature into `Build` does not automatically transition its child steps into `Build` or trigger their agents.
        - Background execution uses `NoOpAgentProcessRunner`, so tasks transition to "Working" without executing real work.
    - **Remaining Work**:
        - Support auto-advancing the first eligible Step when a Feature is moved to `Build`.
        - Connect real agent process runners to the background scheduler loop.

2. **OS Copilot Integration & Window Links**
    - **Status**: **Not Completed**
    - **Target**: Agent execution handler (`CopilotCliProcessRunner.cs`) & UI bindings (`StepCard.razor`).
    - **Action**: Use local OS scripting (e.g., AppleScript / external process triggers via `.NET` `Process.Start()`) or the assigned MCP tools to physically trigger the copilot application in macOS.
    - **Action**: Render an "Open Agent Window" link on the task card in the UI when an agent claims a task.
    - **Current State**: `Program.cs` registers `NoOpAgentProcessRunner`. `CopilotCliProcessRunner` starts copilot CLI headlessly (`CreateNoWindow = true`) without OS application integration or AppleScript terminal spawning. The "Live CLI" link in `StepCard.razor` checks `CurrentRun.SessionLink`, which is null by default.
    - **Remaining Work**:
        - Implement macOS process spawning (e.g., via AppleScript `tell application "Terminal" to do script ...` or opening the desktop copilot GUI app).
        - Expose a real session URL or OS deep-link in `AgentRun.SessionLink`.
        - Update `StepCard.razor` and `FeatureCard.razor` with an "Open Agent Window" link.
