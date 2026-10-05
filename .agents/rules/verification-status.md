---
name: Verification Status
description: Which tools and behaviors were verified against a live Inventor session, and which were not
triggers:
  - Asked what works, what is tested, or what is left to do
  - Planning verification for a change, or choosing the next task
  - Touching the activity feed ring buffer
---

## What is verified, and what is not

Everything below was checked against a live Inventor 2027 session, including a production assembly with
23 open documents, 91 parameters and eight iLogic rules.

Verified: the pipe and main thread dispatcher, the activity feed, parameter reads and writes across numeric, text
and boolean kinds, expression evaluation, iProperty reads and writes, the assembly tree including suppressed
occurrences and the node and depth budget, health reporting including a genuinely sick feature, document targeting
by name, the error paths for a missing document and a wrong document type, `inventor_update`, both execution tools,
the API lookup, and assembly isolation.

Inventor 2025 and 2026 were verified through the loader shim: the session, the API lookup, both execution tools,
and isolation with iLogic's own Roslyn loaded.

On 2026-09-22, on Inventor 2025: `inventor_eval_csharp` with no document open (`Document` null), and the plugin
development loop, which ran a reference plugin's Design Automation path on nine payloads inside the live session.
See `plugin-loop.md`. The `inventor_orientation` no-document message was verified against the rebuilt server.

`inventor_run_plugin` and `inventor_drawing_layout` were verified on 2026-09-22 against the rebuilt server: the
reference plugin's nine payloads run one call each, and all nine generated drawings measured with no issue. Named
binding works against a plugin built with embedded interop types, where `Type` identity does not match.

`inventor_run_plugin`'s log handling that reports only the lines a call wrote was verified later on 2026-09-22
through eleven live calls against the reference plugin's shared rolling log.
The server's warning on an execution result over 20 s was built then too, but no call has run that long since.

`inventor_start` was verified on 2026-09-22 on Inventor 2025, with Inventor closed, by driving the server by hand
over stdio as a 2025-11-25 client. Verified: `already-running`, `version-not-installed`, `version-required` for a
client with no elicitation, the form offering 2025, 2026 and 2027 (the SDK carried it as `elicitation/create`),
`cancelled` on decline, `version-not-offered` for an answer outside the form, a start through the form, a start
through the one release branch with `INVENTOR_MCP_RELEASES=2025`, Explorer as Inventor's parent, and Inventor
surviving both the server's exit and `taskkill /T /F` of the server. The start reached `started` in about 7 s.

Not yet exercised for `inventor_start`: the form inside Claude Code itself, a native 2026-07-28 MRTR client, closing
Claude Code while Inventor runs, `inventor-starting-or-no-bridge`, `addin-not-deployed`, `inventor-exited`,
`still-starting` and `start-failed`. To see `still-starting`, start with a recovery dialog pending.

Not yet run: the plugin loop on Inventor 2027.

Not yet exercised: the ring buffer's `droppedEntries` counter, which needs more than 2000 buffered events.

The blocking dialog watchdog was verified on 2026-09-25 on Inventor 2026.2, through the live level of
`Tests/InventorMcp.Server.Tests` (`INVENTORMCP_LIVE_TESTS=1`): the iLogic error closed through `RunRule` in a
snippet and through `inventor_run_ilogic`, a message box with OK closed, a Yes/No question left open with
`blocked-by-dialog`, the block read by a second client with no bridge call, the next call after a question, one
click from two clients, and the setting off. Two runs in a row passed all six. See
`Docs/Research/Blocking-Dialog-Detection.md`, "Live test results", for what the first runs found.
The migration dialog (`INVENTORMCP_ACCEPT_MIGRATION_DIALOG`) was verified later on 2026-09-25 on Inventor 2025.4,
through the same live level with `INVENTORMCP_LIVE_OLD_RELEASE_PART` set to a 2024.3 part: read through the Win32
fallback, left open by default and closed with Cancel through `WM_COMMAND`, and closed with OK when accepted. All
eight live tests passed in that run. See `Docs/Research/Blocking-Dialog-Detection.md`, "The migration dialog".
Not yet exercised: `inventor_dialogs` and `inventor_dialog_click` from a real MCP client while Inventor is blocked,
an iLogic Security Alert, a dialog of a different process (ex. Vault), and Inventor running as administrator.

The Phase 4 tools of `Docs/Tasks/Usage-Findings-Implementation-Plan.md` were verified on 2026-09-25 on Inventor
2025.4 through the packed server, driven over stdio, on test copies in `C:\Work\_McpTest`
(a Vault workspace project with the library paths `Designs` and `Libraries`):

- `inventor_test_copy`: 28 files of a tree with suppressed components in one pass, 27 references pointed at the
	copies, 10 library references kept, 0 problems. A second call to the same target refused. The registered
	Apprentice was the 2025 in-process DLL, run in a child Windows PowerShell process.
- `inventor_session`: the project, the library paths resolved from the `.ipj` folder, `iLogicRulesEnabled`, and a
	library part as `isModifiable=false`, `readOnlyFile=true`, `library=Designs`.
- A write to that library part through `UserParameters.AddByExpression` failed with the late-bound text "Exception
	has been thrown by the target of an invocation", not E_FAIL. The `CONTEXT:` line named the part as not modifiable.
- `inventor_close_documents` refused 2 dirty documents, then closed 8 under the folder and none outside it.
- `inventor_file_info`, `inventor_features`, `inventor_pattern_elements`, `inventor_hole_check` and
	`inventor_styles` on a sheet metal part and two drawings. The hole check found 20 holes. Without the full circle
	rule it counted 39, because the inside of a bend and the end of a slot are also concave cylinders.
- `inventor_export_sheet_image`: an invisible drawing rendered in 389 ms. `Camera.SaveAsBitmap` ignored the
	camera target, so the image comes from `CreateImageWithOptions` with `IncludeEdits`.
- `inventor_drawing_layout`: three balloons added in memory to a drawing copy, never saved. It flagged the two
	arrowheads at one point and a tip 0.04 in from another component, which the image confirmed.

Later on 2026-09-25, on Inventor 2025.4:

- The six live dialog tests passed two runs in a row on Inventor 2025 (19 s each), with no dialog left open.
- A balloon with a second leader branch (`LeaderNode.AddLeader`) to another component: the layout reported the
	branch as attached to a component the balloon does not name. The check reads every leaf node of the leader.
- The arrowhead curve check on a view with 932 curves and 12 balloons took 1.6 s. With `curveCheckSeconds` 0.1 it
	stopped after 35 curves and said so.
- A headless Claude Code 2.1.282 session, a real MCP client with the newest packed server, called
	`inventor_session`, `inventor_hole_check`, `inventor_file_info` and `inventor_export_sheet_image`, and read the
	sheet image content correctly (the 98.500 dimension).

`inventor_auto_balloon` was checked on 2026-09-26 on Inventor 2025.4 through the packed server from Claude Code, on
a test assembly: all sides, limited sides, a named style, a second view, and a Normal and a Phantom sub-assembly,
each with 0 layout issues. `inventor_drawing_layout` no longer flags a balloon of a sub-assembly whose leader attaches
to one of its parts. See `Docs/Research/Auto-Balloon.md` for what is not yet checked.

The skills (`inventor_skill`) were checked on 2026-09-25 with headless Claude Code 2.1.282 sessions that started
outside this repository, so no repository rule loaded:

- A snippet task (list the text and boolean parameters of a part), with only the list of skills in the
  instructions: no skill was read, and it took 3 snippets, the first with the trap that `interop` describes.
- The same task after the instruction said to read `interop` before the first snippet, and the tool description
  said so too: it read `interop`, then needed 2 snippets, because a returned `List` came back as its type name.
  After the description said to return a string: `interop`, then 1 snippet, with the correct answer (11).
- A task to plan a rule edit, with no change allowed: it planned from the tool descriptions and read no skill.
- A task to do the rule edit on a test copy, after the description of `inventor_ilogic_rule_set` named its skill:
  it read `ilogic-rule-edit` first, then followed its steps (session, read, anchored change with backup and diff,
  read back).

By the user, on 2026-09-25, with the same task on the master part (read only, rules off):

- Claude Desktop, Opus 5.5: "The tool description says to read the interop skill first, so I'll load it." Then
	one snippet with `GetTypeFromString(p.get_Units())`, and the correct 11 parameters.
- Visual Studio, Copilot with Claude Sonnet 5: the correct 11 parameters, but no `inventor_skill` call was visible,
	and it compared the unit text to "Boolean" and "Text". After a restart of the server connection, Visual Studio
	asked to trust the server again and listed the added tools as the change.

The Phase 6 add-in release was verified on 2026-09-25 on Inventor 2025.4, with the add-in built for 2025 and 2026:

- `executed-code.log` has `client='<name> <version>'` in each entry header and a `result` line after the code, with
	the outcome, the exception, the milliseconds, and the documents that a failed snippet left open.
- A snippet that opened a part and then threw returned `documentsLeftOpen` with that part.
- A returned `List<string>` and an anonymous object came back as JSON. `Application.Documents` (a COM object) came
	back as `System.__ComObject`, as before.
- `addin.log` had a line for a refused call (`not-found`).
- The six live dialog tests passed on the new add-in (21 s).
- Not exercised live: the throttle of `All pipe instances are busy`, which the unit tests cover. Inventor 2026 was
	built and deployed, but not started.

Not yet exercised: the `inventor-busy` result for `RPC_E_CALL_REJECTED` and `RPC_E_SERVERCALL_RETRYLATER`.
The dispatcher queues each call on Inventor's main thread, so no call has been rejected yet.
To test it, open a modal dialog (ex. Parameters) or start a large rebuild, then call any tool except
`inventor_activity`. A hang or an unhandled `COMException` is a defect. If rejections occur, add a retry in
`BridgeClient` first, because that needs no add-in rebuild.

A non-empty `sickFeatures` list was seen on 2026-09-23: a hole drilled away from the solid on Inventor 2025 gave
`DriverLost`, an empty message from the add-in, `errorCount` 0, and no new face. The server now fills the message
for `DriverLost` (`AddHealthHints` in `InventorTool.Model.cs`), verified against that hole.
Not yet exercised: a non-empty `errors` list from `inventor_health`.

The tool package and local feed were verified on 2026-09-23 on SDK 10.0.401, by driving `dotnet tool exec` by hand
with a live Inventor 2025 session: the package contents, the server instructions in the `initialize` response, clean
stdout, all 18 tools listed, `inventor_api_lookup` answered from the cache folder, `inventor_session` over a third
pipe connection while two other servers held theirs, a build with no change adding no package, a new session running
the newest version while an old one kept its own, the cleanup keeping a folder in use and removing it on a later
build, a clean exit when stdin closes, and `taskkill /F` of `dotnet tool exec` stopping the server.

Claude Code on the feed was verified on 2026-09-23: the session ran the server from its NuGet cache folder, received
the server instructions and listed 18 tools, and a full `dotnet build InventorMcp.slnx --no-incremental` then
succeeded with the session connected and added a package.
A user scope entry of the same name in `~/.claude.json` must point at the feed too, or sessions in other folders keep
running `bin\Debug`. Claude Code expands `${LOCALAPPDATA}` in the user scope as well (verified with `claude mcp list`
from a folder with no project entry). When the two entries differ in text at all, `/mcp` in this folder shows a
"Conflicting scopes" warning, so both use exactly `${LOCALAPPDATA}/InventorMcp/Feed`.

Inventor survived a kill of `dotnet tool exec` on 2026-09-23: Inventor 2025 started through `inventor_start` from
Claude Code had Explorer as its parent, and `taskkill /F` of that session's `dotnet tool exec` stopped the server
while Inventor kept running.

The "top face" test was run on 2026-09-23 on Inventor 2025 with the prompt "Put a 10 mm hole through the center of
the top face of the open part", in clients that never read this repository's agent instructions. The server logs
show that all three called `inventor_orientation` before any geometry, so the instructions arrived in each:

- Claude Desktop (Opus 5.5): a Hole feature on +Y. It detected its own wrong direction by the unchanged volume,
	deleted the hole, drilled the other way, and called `inventor_health`. Pass.
- Visual Studio Code (Copilot, Sonnet 5): a correct cut with clean health and one new cylindrical face, but as an
	extrude cut, not a Hole feature.
- Visual Studio (Copilot, GPT-5.3-Codex): `Hole4` that removed no material, no `inventor_health` call, and a
	"Done" report. Fail.

The instructions then gained "check health and geometry before you report the work as done" and "use the feature
that the request names". The Visual Studio run again, with that text, still failed with both models: GPT-5.3-Codex
never called `inventor_health` and reported "Done", and Sonnet 5 called it, saw `DriverLost` on `Hole2`, called it
benign, and reported success. Neither hole cut anything. So the instructions now say that `DriverLost` on a new
feature is not benign, and `inventor_health` puts the same explanation in the feature's message.
Run again in Visual Studio: Sonnet 5 read the hint, deleted the hole, drilled the other way, and left `Hole4`, a
healthy Hole feature that cut the part. Pass. GPT-5.3-Codex still called no `inventor_health` and left `Hole5` that
cut nothing. That model is accepted as a known limit, because no text reaches a model that never calls the tool.

Not yet run again in Visual Studio Code with "use the feature that the request names", after it cut the hole with
an extrude. Not yet verified: no `dotnet` or server process left after a client closes normally. Servers driven by
hand exit when stdin closes.

Claude Desktop, Visual Studio and Visual Studio Code each returned a readable `inventor-not-running` with Inventor
closed, and a working `inventor_session` after it started again, with no client restart (2026-09-23).

A server rebuild with all four clients connected (seven servers) succeeded on 2026-09-23.

`dev.watch` on the feed glob (`${env:LOCALAPPDATA}/InventorMcp/Feed/*.nupkg`) did not restart the server on a new
package. Watching `Source/InventorMcp.Server/obj/Debug/LocalFeed.stamp`, a path inside the workspace that the build
touches after it packs, did: a forced repack moved the Visual Studio Code server to a new process on the new version
with no manual step.

On 2026-09-23 the repository's `.mcp.json` and `.vscode/mcp.json` were removed. Each client now has one machine
wide entry (see `README.md`, "Connecting a client"). The results above for Claude Code and Visual Studio Code came
from the repository files. Claude Code's user scope entry is the same command and connected from another folder.

`dev.watch` with `${workspaceFolder}/Source/.../LocalFeed.stamp` in the Visual Studio Code user file did not fire:
the server stayed on its version through three builds. An absolute path to the stamp did not fire either (checked
after a Visual Studio Code restart). The documentation does not say whether `dev` applies to the user file. So
`dev.watch` works only from a workspace file with a path relative to the workspace, and the user entry has no `dev`.
Not yet verified: a "Using the server" entry against a team feed, because no team feed exists.

Not yet verified for `dev.watch`: a restart while a tool call runs.

Found on 2026-09-23: Claude Desktop started the server from `claude_desktop_config.json` and showed it Running.
Visual Studio started it from `%USERPROFILE%\.mcp.json` and listed it once with 18 tools, so that entry wins over
the `.vscode/mcp.json` entry of the same name.

Found on 2026-09-23 on Visual Studio 2026 (18, Professional): it does **not** expand `${env:LOCALAPPDATA}`. The
Copilot log (`%TEMP%\VSGitHubCopilotLogs\`) quoted `dotnet tool exec`: "not found in NuGet feeds
<solution folder>\${env:LOCALAPPDATA}\InventorMcp\Feed", with the full path of the solution folder in place of
`<solution folder>`, so the text was passed through and resolved against the solution folder. The server showed once in the tools picker, not twice. The committed
`servers` key in `.mcp.json` was removed, and the entry moved to `%USERPROFILE%\.mcp.json` with an absolute path.

Found on 2026-09-23 on Visual Studio Code: `.vscode/mcp.json` expands `${env:LOCALAPPDATA}`. The server ran and
listed 18 tools. Its tools picker labels it `InventorMcp.Server`, the name the server reports, not the entry name.
Visual Studio Code also reads the root `.mcp.json` natively (not through `chat.mcp.discovery.enabled`, which has no
Claude Code source) and shows its `mcpServers` entry as Disabled, with a warning that `${LOCALAPPDATA}` is not a
variable it knows. When it did start that entry, `dotnet tool exec` failed on the unexpanded path. Keep it disabled.

Found on 2026-09-23: Claude Desktop starts **two** servers from one entry, one for chat (client `claude-ai`) and one
for agent mode (client `local-agent-mode-autodesk-inventor`). With two Claude Code sessions, Claude Desktop,
Visual Studio and Visual Studio Code open, six servers ran. A server opens its pipe connection only on its first
Inventor call (`BridgeClient.EnsureConnectedAsync`), so an idle one holds none, but six active servers exceed the
add-in's four (`MaxPipeInstances`). Each server logs to its own `server-<date>_<n>.log`, because the first holds
the file.

More than one client, on 2026-09-23 on Inventor 2025, with servers driven by hand beside the real clients:

- The fifth connection failed with the old limit of four: the add-in's accept loop spun with no pause (466,356 log
	lines) and the fifth server got a misleading `inventor-not-running`. See `Docs/Architecture.md`, "A named pipe
	rather than a local HTTP port". The add-in now waits for a free slot, pauses after a failed accept, and allows 16.
- After the fix: 16 connections each connected in about 40 ms. The 17th and 18th got "Inventor is running, but its
	MCP bridge accepted no connection within 3 s", with no hang, and the add-in wrote no log line.
- A waiting call: `inventor_parameters` sent during another server's 4 s snippet waited, then succeeded.
- `inventor_activity` needs no cursor per connection. Two servers both read the feed from sequence 1, because each
	caller keeps its own `sinceSequence` and reading removes nothing.

Claude Code after Inventor closed: the same server returned `inventor-not-running`, and after `inventor_start`
the next call succeeded with no client restart.

Agent instructions, on 2026-09-23 with Claude Code 2.1.280, after `.claude/CLAUDE.md` became the root `AGENTS.md`
and `.claude/rules/` became `.agents/rules/`: a fresh headless session with no file tools listed `AGENTS.md` among
its startup files and knew its content, but could not answer from `build.md` or `verification-status.md`, so no rule
loads at startup. The old `.claude/rules/` loaded all six rules into every session. A second fresh session, allowed
only `Read`, opened `.agents/rules/build.md` for a build question, so a rule loads when its trigger fits.
Not yet verified: Copilot in Visual Studio Code reading `AGENTS.md`.

## Multiple releases (2026-10-05)

Verified:

- The unit level (default run): the release map, the pipe names, `ReleaseSelection` (order, no fallback, a change of
  the choice closes the connection, the legacy pipe) and the reset of `ReleaseYear` and `InventorProcessId`.
- The three add-in targets compile (2025, 2026 and 2027). The 2025 output could not be copied, because Inventor 2025
  had the build folder loaded.
- Against a running Inventor 2025 that still had the old add-in, a server driven by hand returned `bridge-outdated`
  for `inventor_session`, and `inventor_use_release` returned `version-not-supported` for 2099. This shows that
  the enumeration of `\\.\pipe\` finds the legacy pipe, and that the guard stops the model from starting a second
  Inventor.
- `GetActiveObject` and `GetObject` are not used in the source or the tests, so no code can reach the last
  Inventor that registered with COM instead of the one that hosts the add-in.
- The logs of each release go to their own folder (`%LOCALAPPDATA%\InventorMcp\<year>\`). A test run wrote its fake
  dialog clicks to `%TEMP%\InventorMcp.Tests\<process ID>\2025\executed-code.log`, and the real audit trail did not
  change. A server driven by hand wrote "Tool inventor_session finished. Release not connected, selection automatic."
  to its log. The test run deletes its folder when it ends.

Live on 2026-10-05, with Inventor 2026 and 2027 on the redeployed add-in, next to Inventor 2025 on the old add-in:

- Two releases at one time: `inventor_start` in two server processes started 2026, then 2027. Each add-in
  wrote "Bridge started on pipe 'InventorMcp.Bridge.<year>'" to its own `<year>\addin.log`. The .NET 8 loader (2026)
  and .NET 10 (2027) ran together. The legacy pipe of 2025 did not affect the selection.
- With no release chosen, `inventor_session` and `inventor_dialogs` returned `release-required` with 2026 and 2027.
  `inventor_use_release` moved one server between the two releases.
- A dialog in one release did not block another: while a Vault error dialog blocked 2026, a call to 2027
  succeeded. A click on that dialog was audited to `2026\executed-code.log`. A Desktop test with two fixture
  processes was not written, because this live case covers the risk.
- A part created in 2027 did not show in `inventor_documents` of a 2026 session. After Inventor 2026 closed, the
  2027 session kept its connection, and `inventor_session` reported 2027 and bridge version 2.
- Every test level: 180 passed with `INVENTORMCP_RELEASE=2027`, including the migration dialog tests with a part
  saved by 2026. The live level passed with `INVENTORMCP_RELEASE=2026` (8 passed, the 2 migration tests skipped).
- Inventor 2026 starts in more than 45 s here (Vault sign-in), so `inventor_start` returned `still-starting` both
  times, and the bridge answered later.

Not run live: the 2025 add-in on protocol version 2, because Inventor 2025 had the build folder loaded. It is taken to
work like 2026, which uses the same .NET 8 loader and passed every check above.
