Should be able to edit both sides of the dependecy. both sides of the dependency should have a similar view.
all useful messages should be visible as a toast at the bottom of the screen. e.g step created
there does not need to be a notification when a task or ticket is moved between columns
should be able to set dependencies when creating a ticket or task
task popup should just have the status in one place. the legen for status meanings should be on the board and default to closed.
tickets should be editable in the popup unless an agent is actively working on it.
tasks should have dependencies and they should work using the same components and logic as tickets
if a ticket is already in a column before an agent is selected on the column and then an agent is added, then the ticket should get picked up by the agent if the agent criteria is met and the agent is avialable
agent task harness does not need to be shown in the top right
agents are supposed to be defined at the top level, not within a board. 
the menu structure should be boards and agents at the top level. when a board is selected there is a choice between roadmap view and board view.
there should be a consistent breadcrumb structure for all views. the breadcrumb should be at the top of the page separate from the header and should show the current board and agent if applicable.
each page should have a consistent way of showing headings/title bar
board settings do not need to be shown on the main board view. they should be accessible from the board settings menu.
links and buttons should be consistent across the app. for example on the board, roadmap and agnets looks different from settings and new feature.
use tailwind for styling and just use its default look and feel. do not use custom css unless absolutely necessary.
go back to the custom workflow but with restrictions:
- backlog - not ready to work
- ready  - ready for the scheduler to pick up, move to build and assign to an agent
- human review - waiting for human review. skipped if configured
- done - fully complete
- workflow must define exactly one entry point. the scheduler will move tickets from ready to this stage 
- workflows can define multiple transitions to 'done'. the app will enforce whether this goes direct to done or to human review first.
terminals must be shown in the app rather than linking out to a separate window.