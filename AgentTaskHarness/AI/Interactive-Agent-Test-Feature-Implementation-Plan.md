# Implementation Plan: Interactive Copilot Agent Test Harness (POC)

Companion to `Interactive-Agent-Test-Feature-Design.md`. This plan describes **how** the proof of concept will be built and, critically, **how it will be isolated** from the main `AgentTaskHarness` application so it can be iterated on (or thrown away) without any risk to the production app.

---

## 1. Isolation Strategy — New Standalone Project

The POC will **not** live inside `AgentTaskHarness/Components/Pages/`. Instead it gets its own project, sitting next to the existing two:

```
AgentTaskHarness.sln
├── AgentTaskHarness/                 (existing production app — untouched)
├── AgentTaskHarness.Tests/           (existing test project — untouched)
└── AgentTaskHarness.TestAgentPoc/    (NEW — this proof of concept)
```

Rationale:
- **Zero project references** to `AgentTaskHarness.csproj`. The POC does not use `AppDbContext`, EF Core, `IAgentProcessRunner`, or any existing Application/Infrastructure code. It is a self-contained sandbox.
- **Own process, own port.** Runs via `dotnet run --project AgentTaskHarness.TestAgentPoc` on a distinct Kestrel port (e.g. `5299`), so it can be started/stopped independently of the main app and never competes for the same DB file or SignalR hub namespace.
- **Own page, no shared nav.** The POC's only route is `/` (the playground itself) rendered by its own `App.razor` / `Routes.razor` — it does not appear in the main app's `NavMenu.razor` and does not share `_Imports.razor`, `app.css`, or layout components.
- **Disposable by design.** If the POC validates the approach, promote the relevant files into `AgentTaskHarness` deliberately (Phase 7 below). If it doesn't pan out, `git rm -r AgentTaskHarness.TestAgentPoc/` cleanly removes it with no residue in the main app.

The project is still added to `AgentTaskHarness.sln` (as a third `Project(...)` entry) purely for IDE convenience — this does **not** create a build/runtime dependency.

---

## 2. New Project Scaffold

| Item | Value |
| :--- | :--- |
| Project name | `AgentTaskHarness.TestAgentPoc` |
| SDK | `Microsoft.NET.Sdk.Web`, `net10.0` (matches main app) |
| Render mode | Blazor **Interactive Server** (same pattern as main app, no Blazor WASM) |
| Packages | `Microsoft.AspNetCore.SignalR` (built-in), no EF Core, no LibGit2Sharp |
| Port | Configured in its own `Properties/launchSettings.json` (e.g. `http://localhost:5299`) |
| Persistence | None — in-memory `ConcurrentDictionary<Guid, TestAgentSession>` for session state only |

### Folder layout inside the new project
```
AgentTaskHarness.TestAgentPoc/
├── AgentTaskHarness.TestAgentPoc.csproj
├── Program.cs
├── Properties/launchSettings.json
├── Pty/
│   ├── NativePosixPty.cs        (P/Invoke: openpty, posix_spawn, ioctl TIOCSWINSZ, kill)
│   ├── PtySession.cs             (master FileStream + process lifecycle + async pump)
│   └── IPtyService.cs
├── TestAgent/
│   ├── TestAgentSessionManager.cs   (session state, hook callbacks, watchdog)
│   ├── TestAgentSession.cs          (in-memory session record + state enum)
│   ├── AgentWatchdogTimer.cs
│   └── DefaultTestAgentDefinition.cs
├── Hubs/
│   └── TerminalHub.cs             (SignalR hub: stdin/resize in, stdout out)
├── Controllers/
│   └── AgentHookController.cs    (Minimal API or [ApiController] receiving hook POSTs)
├── Components/
│   ├── App.razor
│   ├── Routes.razor
│   ├── _Imports.razor
│   └── Pages/
│       ├── Playground.razor           (@page "/", the only page)
│       └── XtermTerminal.razor         (xterm.js wrapper component)
└── wwwroot/
    ├── app.css                    (minimal Tailwind CDN or plain CSS — not shared with main app)
    └── js/xterm-interop.js
```

This mirrors the design doc's Section 6 breakdown 1:1 but rooted under the new project instead of the main app's `Infrastructure/` and `Application/` folders.

---

## 3. Phased Build Order

### Phase 0 — Scaffold & Solution Wiring
- `dotnet new blazor -n AgentTaskHarness.TestAgentPoc --interactivity Server` at repo root.
- Strip generated sample pages/components down to a blank `Playground.razor` at `/`.
- Add project to `AgentTaskHarness.sln` (IDE convenience only, no `ProjectReference`).
- Configure a distinct port in `launchSettings.json`; confirm `dotnet run` serves the blank page.
- **Acceptance:** `dotnet build AgentTaskHarness.TestAgentPoc` succeeds standalone; main app build/tests remain green and untouched.

### Phase 1 — POSIX PTY Layer
- Implement `NativePosixPty.cs` P/Invoke signatures (`openpty`, `posix_spawn_file_actions_*`, `posix_spawnp`, `ioctl`, `kill`) targeting macOS `libutil`/`libc`.
- Implement `PtySession.cs`: wraps master fd in `SafeFileHandle` → `FileStream`, launches `copilot -i "<prompt>" -C "<targetDir>"`, exposes async read/write and `Resize(cols, rows)`.
- Write a throwaway console smoke test (`Pty/PtySmokeTest.cs` behind a debug-only entry point, or a quick script in `.agent-temp/`) that spawns `echo hello` through the PTY and confirms output round-trips before wiring up SignalR.
- **Acceptance:** A locally-run smoke test proves bytes flow both directions through the PTY and the child process exit code is observable.

### Phase 2 — SignalR Hub + xterm.js Interop
- `TerminalHub.cs`: methods `StartSession(dir, requirements)`, `SendInput(sessionId, data)`, `Resize(sessionId, cols, rows)`, `StopSession(sessionId)`; pushes `ReceiveOutput` events to the caller's connection.
- `XtermTerminal.razor` + `wwwroot/js/xterm-interop.js`: load `xterm.js`/`@xterm/addon-fit` from CDN, wire keystrokes → hub `SendInput`, hub output → `term.write()`, window resize → hub `Resize`.
- **Acceptance:** Typing in the browser terminal round-trips through a locally spawned shell (e.g. `/bin/bash`) before switching the spawned command to `copilot`.

### Phase 3 — Hook Controller & Ephemeral Hook Config
- `AgentHookController.cs`: `POST /api/test-agent/hook/{sessionId}/{eventName}` reading JSON body, forwarding to `TestAgentSessionManager`.
- On session start, `TestAgentSessionManager` writes a temp hooks file to `AgentTaskHarness.TestAgentPoc/.agent-temp/test-hooks-{sessionId}.json` (gitignored) per the template in the design doc §3, substituting the POC's own port.
- Pass the hooks file to the CLI via `--add-dir` / project-local `.github/hooks/` as documented.
- **Acceptance:** Manually triggering `copilot` against a scratch directory shows hook POSTs arriving and being logged by the controller.

### Phase 4 — Session State Machine & Watchdog
- `TestAgentSession.cs`: state enum (`Idle, Initializing, Thinking, ToolExecuting, WaitingForApproval, WaitingForUser, Stalled, Failed, Finished`).
- `TestAgentSessionManager.cs`: applies the state transition table from design doc §4 on each hook event and PTY byte activity.
- `AgentWatchdogTimer.cs`: per-session timer checking `LastActivityTimestamp`; flips to `Stalled` after configurable inactivity (default 180s); exposes an `Interrupt()` that writes `Ctrl+C` (`0x03`) into the PTY.
- **Acceptance:** Deliberately stalling a session (e.g. break the hook curl target) triggers the `Stalled` badge within the configured timeout.

### Phase 5 — Playground Page UI
- `Playground.razor`: target directory + requirements text inputs, Start/Stop/Clear buttons, status badge bound to `TestAgentSession.State`, embeds `XtermTerminal`.
- Wire the default agent definition (`DefaultTestAgentDefinition.cs`) merged with user-entered requirements as documented in design doc §5.
- **Acceptance:** Full manual run — enter a real project directory + a small task, start the agent, observe status badge transitions live, interact with an `ask_user` prompt through the terminal, stop cleanly.

### Phase 6 — End-to-End Verification
- Run against a real disposable scratch repo (not `AgentTaskHarness` itself) to avoid the agent editing its own harness.
- Verify: session start, tool-approval prompts, `ask_user` round-trip, watchdog stall + interrupt, normal completion (`sessionEnd`), and process cleanup (no orphaned `copilot` processes after Stop).
- **Acceptance:** All state transitions in the design doc's Mermaid diagram are observed at least once in a single manual session.

### Phase 7 — Promotion Decision (Post-POC)
- Once validated, decide per-component whether to promote into the main `AgentTaskHarness` project (e.g. move `Pty/` into `Infrastructure/Pty/`, add a real nav entry) or keep it a standalone internal tool.
- Not part of the POC scope itself — recorded here only so the isolation strategy has a clear exit path.

---

## 4. Risks & Open Questions
- **macOS-only P/Invoke**: `openpty`/`posix_spawn` signatures target macOS `libc`; Linux works with the same libc calls but hasn't been verified, Windows is unsupported by this approach entirely.
- **`copilot` CLI must be on `PATH`** (or hardcode `/opt/homebrew/bin/copilot`) on the machine running the POC.
- **Local-only hook security**: `AgentHookController` binds to the same Kestrel instance and is reachable at `127.0.0.1:{port}` with no auth — acceptable for a local POC, not for anything beyond that.
- **Orphaned processes**: `PtySession` must reliably `kill()` the child on Stop/Dispose/app-shutdown; verify no zombie `copilot` processes survive a `Ctrl+C` of the POC host.
- **Port collision**: pick a POC port unlikely to clash with the main app's dev port and note it in `launchSettings.json`.

## 5. Non-Goals for the POC
- No database/persistence of sessions (in-memory only, lost on restart).
- No multi-user/multi-session concurrency hardening.
- No integration with the main app's nav, auth, or design system.
- No Windows/Linux verification (macOS dev machine only).
