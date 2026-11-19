# Live Verification

Created: 2026-10-07. Procedures added: 2026-11-16.

Status: **open.** These behaviors are built, and the unit and desktop tests cover them where they can, but no live
Inventor session has shown them yet. Check an item off when a live run shows it, and record any design fact that
the run finds in `Docs/Architecture.md`. Delete this document when every item is done or dropped.

This is a list of checks, not a feature. The review is in `Docs/Research/Task-Analysis.md`, "Live verification".

## How to run a session

- Read `.agents/rules/build.md` first. Use test files only (ex. a new part), never a client file.
- Each item has: **Do** (the steps), **Expect** (the result that checks the item off), and **If not** where a
	different result needs a decision.
- Where an item can be a test of the `Live` level (`Tests/InventorMcp.Server.Tests/Live`), it says "Automate".
	Add the test, and the item is then done by the test.
- Read the logs in `%LOCALAPPDATA%\InventorMcp` (`server-<date>.log`, and `<year>\addin.log`) after each item.
- Record measurements in `Docs/Architecture.md` or `Docs/Research/`, not here.

One order that needs the fewest restarts: the items that need Inventor closed ("Starting Inventor"), then the
bridge items, then the dialog items, then the checks of the other plans.

## Starting Inventor

- [ ] `inventor_start`: the release form inside Claude Code itself. A first try got no answer from the client.
	Find why first, see `Docs/Architecture.md`, "The server waits for Inventor, not the model".
	- **Do:** with two releases installed and Inventor closed, call `inventor_start` with no version. Read in the
		server log if the client declared elicitation for that request.
	- **Expect:** the client shows a form, and the retry starts the chosen release.
	- **If not:** when the client does not declare elicitation, the tool must return `version-required`. Record
		the client version and the result.
- [ ] `inventor_start`: a native 2026-07-28 MRTR client.
	- **Do:** the same call from a client that sends the base protocol 2026-07-28 (ex. MCP Inspector).
	- **Expect:** an input request, then a start after the answer.
- [ ] `inventor_start`: close Claude Code while Inventor runs, and check that Inventor keeps running.
	- **Do:** start Inventor with the tool, then close the client window, then end the client process tree.
	- **Expect:** Inventor still runs, and its parent process is Explorer.
- [ ] `inventor_start` results: `inventor-starting-or-no-bridge`, `addin-not-deployed`, `inventor-exited` and
	`start-failed`.
	- **Do:** one case for each: Inventor open with the add-in turned off, the manifest of one release removed
		for the test, Inventor ended in Task Manager during the start, and an executable path that cannot start.
	- **Expect:** each code, with a message that gives the next step. Put the manifest back.
- [ ] `inventor_session` with `waitSeconds`: the early end when the pipe belongs to a different release, or to a
	process that is not this user's Inventor.
	- **Do:** choose release A with `inventor_use_release` while only release B runs, and call with
		`waitSeconds` 30. For the second case, host the pipe name with a small test program before Inventor starts.
	- **Expect:** an answer in less than 5 s, with `inventor-not-running` for the first and
		`untrusted-bridge-host` for the second.

## Dialogs

- [ ] With no `DialogSettings.json`, an iLogic Security Alert stops the call with `blocked-by-dialog`.
	- **Do:** rename the user file and restart the server. Run a rule that reads the registry.
	- **Expect:** `blocked-by-dialog` with the text and the buttons. No click.
- [ ] With the user file set to answer them, the watchdog answers the Security Alert and the Security Advisor by
	itself in a live call.
	- **Expect:** the call succeeds, and `blockingDialogs` lists each click and what it changed.
- [ ] A Security Alert for a rule inside a document, and the text of its Security Advisor, which can differ from
	the Advisor of an external rule.
	- **Do:** the same with a rule in a part. Record both dialog texts in `Docs/Research/Blocking-Dialog-Detection.md`.
- [ ] Whether an edit that adds new system code (ex. a new `Imports` or a registry call) keeps the trust of a
	rule. An edit that adds only a comment keeps it.
	- **Do:** trust a rule, change it with `inventor_ilogic_rule_set`, and run it.
- [ ] The "Microsoft .NET" error ("Cannot access a disposed object") after an iLogic error: the server clicks
	`Continue`. The desktop fixture cannot show this dialog.
- [ ] A dialog of a different process (ex. Vault).
	- **Expect:** `blocked-by-dialog` with the text "its main window is disabled, but it has no dialog", after two
		checks.
- [ ] Inventor running as administrator.
	- **Do:** start Inventor elevated and the client not elevated. Call `inventor_session`, then open a dialog and
		call `inventor_dialogs` and `inventor_dialog_click`.
	- **Expect:** the connection works. Record if the read and the click work (UIPI can stop the click).

## Bridge and results

- [ ] The `inventor-busy` result for `RPC_E_CALL_REJECTED` and `RPC_E_SERVERCALL_RETRYLATER`. Open a modal dialog
	(ex. Parameters) or start a large rebuild, then call any tool except `inventor_activity`. A hang or an
	unhandled `COMException` is a defect. If rejections occur, add a retry in `BridgeClient` first, because that
	needs no add-in rebuild.
- [ ] The server's throttle of `All pipe instances are busy`.
	- **Do:** connect 16 servers (ex. 16 test clients that hold a connection), then a 17th.
	- **Expect:** no line flood in `addin.log`, and `bridge-connections-in-use` for the 17th.
	- **Automate:** the end-to-end test in `Docs/Tasks/Bridge-JsonRpc-Migration.md` covers it with no Inventor.
- [ ] The causes of a failed connection (`Docs/Architecture.md`, the table of causes).
	- **Do:** turn the add-in off in the Add-In Manager, and call `inventor_session` more than 120 s after the
		start. Then call `inventor_session` during a start of Inventor. Then close Inventor while a call waits.
	- **Expect:** the "not loaded" message, then the "loading" message followed by a wait that succeeds, then a
		server log line that says that Inventor closed.
- [x] Two Inventor processes of the same release, both with the add-in.
	- Each process has its own pipe, and the live tests show it (see `Docs/Research/Bridge-Pipe-Live-Checks.md`).
- [ ] The `droppedEntries` counter of the activity feed, which needs more than 2000 buffered events.
	- **Do:** a snippet that makes more than 2000 transactions in several calls under 10 s each (ex. a parameter
		write in a loop on a new part), then `inventor_activity` with `sinceSequence` 0.
- [ ] A non-empty `errors` list from `inventor_health`.
	- **Do:** a part with a feature that fails after a parameter change, then `inventor_update` and
		`inventor_health`.
- [x] The server's warning on an execution result over 10 s.

## Clients and tools

- [ ] The plugin loop on Inventor 2027.
- [ ] Visual Studio Code: the "top face" prompt again, after the instruction "use the feature that the request
	names". It last cut the hole with an extrude.
- [ ] No `dotnet` or server process is left after a client closes normally.
	- **Do:** note the server process ids, close each client, wait 10 s, and list the processes.
- [ ] A "Using the server" entry against a team feed, when a team feed exists.
- [ ] `dev.watch`: a restart while a tool call runs.
- [ ] Copilot in Visual Studio Code reads `AGENTS.md`.
- [ ] `inventor_auto_balloon`: the open items in `Docs/Research/Auto-Balloon.md`.
- [ ] The tool hints in clients. No Inventor is necessary.
	- **Do:** in Claude Code and in Visual Studio Code, call a read tool and a write tool with the default
		permission mode.
	- **Expect:** no fixed result. Record what each client asks in `Docs/Architecture.md`. The documentation of
		Visual Studio Code says that `readOnlyHint` changes the default confirmation.
- [ ] The `isError` flag in clients.
	- **Do:** in Claude Code and in Visual Studio Code, call `inventor_parameters` with a `documentName` that does
		not exist. In Claude Code, call `inventor_session` with Inventor closed.
	- **Expect:** the model reads `not-found` and calls `inventor_documents`. `inventor_session` is not shown as a
		failed call.
	- **If not:** if a client hides the body of an error result from the model, the flag must stay off for that
		client, or for all. Record it and stop.

## Checks that the other plans need

These are in their own documents, with the decision that each result changes. One Inventor session can do all of
them.

| Plan | Checks |
| --- | --- |
| `Docs/Tasks/Document-Targeting.md`, "Checks before the code" | `ActiveEditDocument` during an edit in place and with none, two model states of one file, the format of `FullDocumentName` |
| `Docs/Tasks/Dedicated-Tool-Candidates.md` | Which `SelectSet` has the selection during an edit in place, and the saved release of an open document |
