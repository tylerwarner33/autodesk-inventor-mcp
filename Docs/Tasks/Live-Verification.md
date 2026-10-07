# Live Verification

Created: 2026-10-07

Status: **open.** These behaviors are built, and the unit and desktop tests cover them where they can, but no live
Inventor session has shown them yet. Check an item off when a live run shows it, and record any design fact that the
run finds in `Docs/Architecture.md`. Delete this document when every item is done or dropped.

## Starting Inventor

- [ ] `inventor_start`: the release form inside Claude Code itself.
- [ ] `inventor_start`: a native 2026-07-28 MRTR client.
- [ ] `inventor_start`: close Claude Code while Inventor runs, and check that Inventor keeps running.
- [ ] `inventor_start` results: `inventor-starting-or-no-bridge`, `addin-not-deployed`, `inventor-exited` and
	`start-failed`.
- [ ] `inventor_session` with `waitSeconds`: the early end when the pipe belongs to a different release, or to a
	process that is not this user's Inventor.

## Dialogs

- [ ] With no `DialogSettings.json`, an iLogic Security Alert stops the call with `blocked-by-dialog`.
- [ ] With the user file set to answer them, the watchdog answers the Security Alert and the Security Advisor by
	itself in a live call.
- [ ] A Security Alert for a rule inside a document.
- [ ] A dialog of a different process (ex. Vault).
- [ ] Inventor running as administrator.

## Bridge and results

- [ ] The `inventor-busy` result for `RPC_E_CALL_REJECTED` and `RPC_E_SERVERCALL_RETRYLATER`. Open a modal dialog
	(ex. Parameters) or start a large rebuild, then call any tool except `inventor_activity`. A hang or an unhandled
	`COMException` is a defect. If rejections occur, add a retry in `BridgeClient` first, because that needs no
	add-in rebuild.
- [ ] The server's throttle of `All pipe instances are busy`.
- [ ] The `droppedEntries` counter of the activity feed, which needs more than 2000 buffered events.
- [ ] A non-empty `errors` list from `inventor_health`.
- [ ] The server's warning on an execution result over 20 s.

## Clients and tools

- [ ] The plugin loop on Inventor 2027.
- [ ] Visual Studio Code: the "top face" prompt again, after the instruction "use the feature that the request
	names". It last cut the hole with an extrude.
- [ ] No `dotnet` or server process is left after a client closes normally.
- [ ] A "Using the server" entry against a team feed, when a team feed exists.
- [ ] `dev.watch`: a restart while a tool call runs.
- [ ] Copilot in Visual Studio Code reads `AGENTS.md`.
- [ ] `inventor_auto_balloon`: the open items in `Docs/Research/Auto-Balloon.md`.
