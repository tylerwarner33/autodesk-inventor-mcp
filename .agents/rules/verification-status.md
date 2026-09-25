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
Not yet exercised: `inventor_dialogs` and `inventor_dialog_click` from a real MCP client while Inventor is blocked,
an iLogic Security Alert, a dialog of a different process (ex. Vault), and Inventor running as administrator.

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
