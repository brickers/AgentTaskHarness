# Agent Task Harness Solution Design

## Platform

- Cross-platform C# app; runs on Windows, Linux, and macOS.
- All board, column, feature, step, dependency, and agent-definition data is persisted locally in SQLite.
- Uses Blazor for the web UI.

## Hierarchy Overview

- Three levels: Roadmap (organisational only, no board of its own) → Feature/ticket (has its own board) → Step/dev-plan-step (nested one level below its Feature).
- A Feature contains requirements, acceptance criteria, etc., plus a dev plan: a visual dependency tree of its Steps showing each Step's current status.
- A Step contains only the bits needed to carry out that step (see Agent Context for what an agent actually receives).

## Roadmap

- Each board has exactly one Roadmap sitting above it (one Roadmap per board); the Roadmap has no board/columns of its own.
- The Roadmap shows a dependency-tree/plan visual of its board's Features, each annotated with its current status — the same visual pattern a Feature uses for its own Steps.
- Since a Roadmap belongs to exactly one board, the Feature dependencies described in Feature & Step Dependencies are always scoped to that single board.

## Boards & Fixed Workflow

- Multiple, fully independent Kanban boards; each has its own Features and Steps, fully isolated from other boards. Agents themselves are defined globally, independent of any single board (see Agent Definition & Composition).
- Every board uses the same fixed, hardcoded six-column workflow for both its Features and its Steps: **Backlog → Ready → Build → Agent Review → Human Review → Done**. Columns cannot be created, removed, reordered, or otherwise reconfigured — see Column Transition Rules for how cards move between them.
- A Feature's cards and its Steps' cards move through two separate instances of this workflow: one column workflow tracks each Feature, and one — shared by all Features on that board — tracks the Steps nested inside each Feature.
- Web UI supports full CRUD (create, read, update, delete) for Features and Steps; only the fixed column structure itself is not configurable.
- Agents cannot be assigned to Backlog, Ready, Human Review, or Done columns. On the Step workflow, both Build and Agent Review can have one or more agents assigned to them. On the Feature workflow, only Agent Review can have agents assigned — a Feature's Build column never has an agent assigned to it (see Features, Agent Definition & Composition, and Agent Scheduling & Concurrency).
- A board has two independent "skip Human Review" toggles — one for its Feature workflow, one for its Step workflow. When a toggle is off, cards in that workflow auto-advance from Agent Review straight to Done; when on, they stop in Human Review for a human to manually advance to Done.
- Regardless of the board-level toggle, an individual Feature or Step can be flagged to always require Human Review, overriding the board setting for that card only.
- A card only counts as complete once it reaches Done.
- Features can only be created in their board's Backlog column; Steps can only be created in their Feature's Backlog column.
- A Feature or Step with a running agent cannot be deleted; the agent must be stopped first.

## Column Transition Rules

- Forward moves are adjacent-only, one column at a time, in sequence: Backlog → Ready → Build → Agent Review → Human Review → Done.
- From Agent Review or Human Review, a card can move backward directly to Build (e.g. to send it back for rework).
- From any column except Done, a card can move backward directly to Backlog or Ready.
- Done is terminal: cards never leave Done once reached.
- These rules apply identically to Feature cards and Step cards, subject to the additional gating conditions described in Features, Steps (Dev Plan Steps), and Feature & Step Dependencies (e.g. dependency checks, agent-running locks, concurrency queueing).

## Features

- Features hold the requirements, acceptance criteria, and other ticket-level content, plus the dev plan described in Hierarchy Overview.
- Moving a Feature from Backlog to Ready checks whether all of that Feature's Feature-level dependencies are complete (see Feature & Step Dependencies); the move is blocked if any are unmet.
- Once a Feature successfully moves to Ready, all of its Steps are moved to Ready together as one batch, regardless of each Step's own dependency status — Ready means "the content is settled and agreed to be worked on." A Step's own dependencies only gate its later move from Ready into Build (see Steps and Feature & Step Dependencies).
- When the first of a Feature's Steps is moved into Build, the app automatically advances the Feature card itself from Ready to Build, reflecting that active work has begun (see Git / Version Control Workflow for the branch/worktree created at this point). This automatic advance is the only way a Feature ever enters Build (see Agent Scheduling & Concurrency for why the scheduler never starts a Feature directly, and Boards & Fixed Workflow for why no agent is ever assigned to it while it's in Build).
- Once all of a Feature's Steps reach Done, the app automatically moves the Feature from Build to Agent Review.
- These Feature/Step transitions are fixed, hardcoded application behavior (not a general configurable rule/trigger system).

## Steps (Dev Plan Steps)

- A Step holds only the information described in Hierarchy Overview (see Agent Context for what's actually passed to the agent working on it).
- Steps move through their Feature's nested Step column workflow (see Boards & Fixed Workflow), following the same fixed six columns and Column Transition Rules.
- A Step sitting in Ready becomes eligible to move into Build only once all of its Step-level dependencies are complete (see Feature & Step Dependencies); the move into Build itself only happens once the scheduler has actually picked the Step up and started an agent on it (see Agent Scheduling & Concurrency).
- A Step's branch/worktree is created when it enters Build, not before (see Git / Version Control Workflow).

## Feature & Step Dependencies

- Step dependencies only apply within the same Feature; Feature dependencies only apply within the same board (and, since each board has exactly one Roadmap, within that Roadmap too).
- Steps can depend on other Steps in the same Feature; Features can depend on other Features on the same board.
- A Feature can't move from Backlog to Ready until all its Feature dependencies are complete (merged to main). A Step can't move from Ready to Build until all its Step dependencies are complete (merged into the Feature branch).
- Once a Step or Feature has started (entered Build), its dependency list is locked; it can only be edited after moving it back to its Backlog.
- Completed (merged) Steps/Features are terminal — they never move backward.
- Returning a started Step/Feature to its Backlog to edit its dependencies also affects its branch/worktree (see Git / Version Control Workflow).

## Review Outcomes & Failure Tracking

- Each Feature and each Step keeps two persistent counters: how many times Agent Review has sent it back to Build, and how many times Human Review has sent it back to Build. These counters never reset, even if the card is later moved back to Backlog.
- A card in Build whose counters are greater than zero is treated as "rework" rather than a fresh, first-time-in-Build card; this is a derived indicator backed by the stored counters, not a separate column or workflow state (see UI & Status Visibility for how this is displayed).
- Each board has a configurable "too many failures" threshold, applied independently to the Agent Review counter and the Human Review counter. Once either of a card's counters reaches its board's threshold, the app stops assigning it an agent, and the scheduler skips it (see UI & Status Visibility for the issue indicator shown to the human).

## Comments & Activity Log

- Every Feature and every Step has its own read/write comments section, independent of its parent's/children's comments (a Step's comments are separate from its Feature's comments).
- Both agents (dev and review) and human users can read and post comments; this doubles as a communication channel (e.g. a review agent leaving feedback for a dev agent to read) and a permanent activity history of what each participant did on the card.

## Interfaces & Access

- Boards are accessible to humans via a web UI and to agents via MCP.
- Via MCP, agents cannot request a raw column move. Instead, MCP exposes a curated set of named actions (e.g. "start build", "submit for review", "approve review", "fail review", "send to backlog") that each map internally to the relevant column transition and any associated bookkeeping (e.g. incrementing a failure counter).
- Via MCP, agents can also read Step/Feature details, query which named actions are currently available for a card, and read/write that card's comments (see Comments & Activity Log).
- Single-user system; no authentication.

## Agent Scheduling & Concurrency

- Only Build and Agent Review invoke agents; Backlog, Ready, Human Review, and Done never run an agent. Ready is purely a signal that a card's content is settled and agreed to be worked on — it does not itself start anything.
- A board can register multiple agent definitions on its Step workflow's Build column, multiple on its Step workflow's Agent Review column, and multiple on its Feature workflow's Agent Review column (a Feature's Build column never has agent definitions registered on it — see Boards & Fixed Workflow). Each registered definition can carry its own matching criteria for the app to pick the right one per card (see Agent Definition & Composition).
- The scheduler only ever starts Steps — it never starts a Feature directly. It identifies the next eligible Step to work on (a Step in Ready with all Step dependencies met) using the prioritization algorithm below, then moves that Step to Build and assigns it a matching agent definition. A Feature entering Build is purely the automatic side effect of its first Step starting (see Features), not a scheduler-initiated action.
- A configurable, per-board limit caps how many agents can run concurrently; if the limit is already reached, an eligible card queues and is started automatically once a slot frees up.
- If a card changes column (via an MCP named action) while its current agent is still running, the card is flagged (e.g. "blocked: previous agent still active") until the outgoing agent finishes or is manually stopped.
- Users are able to move cards according to the Column Transition Rules, but cannot move a card into Build or Agent Review if its agent is still running.
- A prioritization algorithm decides which eligible Step the scheduler starts next, taking into account factors such as how far along it already is toward completion, how much work remains until it's finished, its age, and similar factors; exact weighting is to be tuned later.
- On agent failure, the card is marked "Failed"; retry is manual only — no automatic retry.

## Agent Definition & Composition

- Agents are defined globally, independent of any board; the same agent definition can be assigned to a Step workflow's Build or Agent Review column, or to a Feature workflow's Agent Review column, on multiple boards. A Feature workflow's Build column never accepts an agent-definition assignment (see Boards & Fixed Workflow).
- Agents are defined by composing reusable agent components (add, remove, reorder).
- An agent definition assigned to one of these columns can carry matching criteria (e.g. ticket size, new build vs. returned-after-failed-review, model choice), so a board can register several agent definitions per column and let the app pick the right one per card (see Agent Scheduling & Concurrency).
- Saving an agent definition in the UI generates a definition folder on disk with the composed configuration.

## Agent Execution Environment

- Agents run via the copilot CLI inside the card's dedicated git worktree (a Step's worktree, or a Feature's worktree when running in its Agent Review column), not the main repo checkout.
- The folder path is passed to the agent on invocation via the copilot CLI.
- Only read-only git commands are permitted (e.g. `git diff`, `git log`, `git status`); commit/push/checkout-modifying commands are blocked.

## Agent Context

- When an agent is invoked on a Step, it is given: the Step's own description; the relevant subset of the parent Feature's requirements, acceptance criteria, and in/out-of-scope notes; the relevant parts of the Feature's suggested solution; and free-text guidance notes to keep the agent on track (e.g. "don't do this — a future step handles it").
- How this per-step context gets authored/curated (e.g. ticket templates) is a user workflow concern rather than a fixed architectural requirement, and is left open for future refinement.

## Git / Version Control Workflow

- Each board matches a single git repo.
- A Feature gets its own branch and worktree, named after the Feature id, branched off main, created automatically when the Feature enters Build (triggered by its first Step entering Build — see Features).
- A Step gets its own branch and worktree, named after the Step id, branched off its Feature's branch (not main), created automatically when the Step itself enters Build.
- Every column transition triggers an automatic commit of pending changes in the card's worktree.
- Reaching a Step's terminal/"Done" column merges the Step branch into the Feature branch and deletes the Step's worktree; the Step then becomes immutable.
- Reaching a Feature's terminal/"Done" column merges the Feature branch into main and deletes the Feature's worktree; the Feature then becomes immutable.
- Returning a started Step/Feature to its Backlog (e.g. to edit its dependencies — see Feature & Step Dependencies) lets the user choose to discard the branch/worktree or keep it paused for later resumption.
- If the branch/worktree was kept paused, resuming the Step/Feature (re-entering Build) reuses that same branch/worktree rather than creating a new one.
- If a merge conflict occurs (Step → Feature branch, or Feature → main), the card's status indicates to the user that input is needed. The user can resolve the issue manually outside the harness, then click a button in the UI to resume the merge and complete the Step/Feature.

## UI & Status Visibility

- In the column/board view, a card shows only its title and status icon; per-card move controls are not shown inline — they live in a popup opened from the card (see below).
- Clicking a card opens a popup with its full details, including its available/allowed moves per the Column Transition Rules (and any per-card Human Review override), its comments/activity log (see Comments & Activity Log), and its Agent Review/Human Review failure counters.
- The column/board view supports drag-and-drop: dragging a card highlights the columns/slots it can legally be dropped into (per the Column Transition Rules and the agent-running lock), and dropping outside a highlighted target is rejected.
- A combined Feature/Step view shows the Feature's fixed columns as a top row and the (shared) Step columns as a bottom row; selecting a Feature card in the top row filters the bottom row to show only that Feature's Steps.
- When an agent is active on a Step or Feature, its card shows a clickable link to the live copilot CLI session.
- A status icon reflects the agent's state: working, waiting for input, completed, failed, blocked, etc.
- A Feature's card additionally shows miniature status indicators mirroring its Steps' status icons, so their blinking/changing state gives a live overview of progress or issues across the whole Feature.
- A card in Build shows a "rework" badge when either of its failure counters is greater than zero, distinguishing cards returned after a failed review from ones being built for the first time (see Review Outcomes & Failure Tracking).
- A card whose failure counter has reached its board's configurable threshold shows a distinct issue indicator in its status area (see Review Outcomes & Failure Tracking).

## Agent Control

- In the UI users can manually stop an agent, which kills the copilot CLI process.
- In the UI users can click a button to remove all uncommitted changes in the card's worktree.

## Cost & Quality Tracking

- Token/API usage and time spent are recorded per Step and aggregated per Feature.
- Additional signals to help judge whether a Feature/Step was well-formed (e.g. whether its original content was sufficient to reach the finished result without much back-and-forth) are wanted; the concrete metrics (e.g. rework/return-to-backlog counts, agent-reported missing-information events) are not yet decided — open question for a future iteration.
