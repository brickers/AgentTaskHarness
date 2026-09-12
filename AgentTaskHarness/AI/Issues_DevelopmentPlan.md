# Agent Task Harness - Development Action Plan

This plan breaks down the 15 issues from your `Issues.md` into logical, manageable phases. By following this sequential approach, you can iterate from quick UI wins to complex backend flows and system integrations.

## Phase 1: UX/UI Polish & Quick Wins
*These tasks improve immediate usability without requiring architectural changes.*

1. **Fix Checkbox Alignment**
   - **Target**: `wwwroot/css` & related Razor components.
   - **Action**: Fix CSS alignment for checkboxes so tick boxes and their labels align horizontally (e.g., using `display: flex; align-items: center;`).

2. **Consistent Menu Navigation**
   - **Target**: `MainLayout.razor` / `NavMenu.razor` (likely in UI/Components layout).
   - **Action**: Unify navigation across the entire app so all pages have access to the same sidebar/header.

3. **Status Indicator Explanations**
   - **Target**: Task/Ticket popup viewer components.
   - **Action**: Add tooltips (`title` attributes or Blazor tooltip components) and a legend in the popup view detailing what each status indicator means.

4. **Separate Row Clicking vs. Editing (Features)**
   - **Target**: Kanban/Board column component.
   - **Action**: Remove the `onclick` handler on the entire feature row that opens the popup. Assign the filtering logic to the row click, and create an explicit "Edit" button icon that explicitly opens the popup.

5. **Clear Feature Filter (Unselect)**
   - **Target**: Kanban/Board view component.
   - **Action**: When a feature is clicked/filtered, display a clear "X" or "Clear Filter" button at the top of the board to unselect the feature and reveal all steps again.

## Phase 2: Data Model & Agent Management
*Focuses on fixing database models and editing configurations.*

1. **Agents in Database, not Hard Drive**
   - **Target**: `Domain/AgentDefinition.cs`, `Infrastructure/DbContext`.
   - **Action**: Remove the directory/folder requirement from the agent definition model. Migrate all defining configurations (prompts, tools) to properties on the Entity. Add EF Core migration for the database schema update.

2. **Editable Agent Components**
   - **Target**: Agent components (likely under `Components/Agents`).
   - **Action**: Add forms mapped to the new database-driven `AgentDefinition` to allow users to create, update, and manage agents visually.

3. **Move Agent Assignment to Column View**
   - **Target**: Column Configuration & Agent Views.
   - **Action**: Remove agent assignments from the agents' main view. Instead, add a dropdown/assignment UI within the Column settings to assign specific agents to handle tasks when they land in a particular column.

## Phase 3: Validation, Paths & Board Drag-and-Drop
*Improves data validation and core board interactions.*

1. **Fix Browser Folder Selection (Board Creation)**
   - **Target**: Board creation component.
   - **Action**: Web browsers restrict retrieving absolute paths for security (throwing standard support errors). If a true path is needed on the backend, replace the `<input type="file" webkitdirectory />` with a direct `<input type="text" />` letting the user paste the path manually, or build a server-side tree browser.

2. **Git Repository Validation**
   - **Target**: Board creation logic (`Application`/`Infrastructure` layers).
   - **Action**: Before saving the board, use `LibGit2Sharp` to validate if the given path contains a valid `.git` repository (e.g., `Repository.IsValid(path)`). If false, add a validation error message in the UI preventing creation.

3. **Kanban Drag and Drop**
   - **Target**: Board/Column/Task UI components.
   - **Action**: Implement drag and drop (using HTML5 native drag events or a library). Add CSS classes to highlight valid destination columns when a card is dragged over them, and visually block invalid ones based on state transition rules.

## Phase 4: Roadmap & Dependencies
*Adding higher-level project management features.*

1. **Create Features on Roadmap**
   - **Target**: Roadmap UI.
   - **Action**: Add a "Create Feature" button and form accessible directly from the Roadmap timeline view.

2. **Feature Dependencies (Backend & UI)**
   - **Target**: `Feature` Domain model.
   - **Action**: Add a many-to-many self-referencing relationship (e.g., `DependsOn`, `RequiredBy`). Add UI on the feature details popup to select dependencies. 

3. **Dependency Graph/Visualization**
   - **Target**: Roadmap viewer.
   - **Action**: Introduce a visual graph representation for feature dependencies (consider Mermaid.js via JSInterop) and list dependencies explicitly in the detailed view of both the dependent and dependee.

## Phase 5: Agent Automation & OS Integrations
*Completing the closed loop with the physical OS agents.*

1. **Workflow Pipeline Triggers**
   - **Target**: Stage transition services (State Machine / Hooks).
   - **Action**: Wire up the logic so that moving a feature and its steps into 'Ready' -> 'Build' triggers the assigned agent automatically in the backend worker process. The UI should reflect the status change visually (e.g., "Queued" -> "Agent Working...").

2. **OS Copilot Integration & Window Links**
   - **Target**: Agent execution handler & UI bindings.
   - **Action**: Use local OS scripting (e.g., AppleScript / external process triggers via `.NET` `Process.Start()`) or the assigned MCP tools to physically trigger the copilot application in macOS. 
   - **Action**: Render an "Open Agent Window" link on the task card in the UI when an agent claims a task.
