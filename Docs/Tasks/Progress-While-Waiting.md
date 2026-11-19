# Progress While a Call Waits

Created: 2026-11-19.

Status: **open, client checks first.** Server only. No add-in build. Not part of the bridge version 3 release: it
works with the current bridge protocol and with MCP over the pipe, so it can ship before or after that release.

## Goal

A tool call that waits behind another bridge call, or waits for Inventor, tells the client why it waits. The model
then sees ex. "Waiting behind evalCSharp for 12 s" and does not take the server as hung. Nothing changes the order of
the calls or what a call does.

## Facts for the implementer

- `BridgeClient` runs one bridge call at a time for each server process. `_gate` (`SemaphoreSlim(1, 1)`) is held for
	the full call: the send, the wait for the response, the dialog watch and the reconnect
	(`Source/InventorMcp.Server/Bridge/BridgeClient.cs`, `InvokeAsync`). A second call waits at `_gate` with no report.
- The time at `_gate` is already measured: `ServerTelemetry.RecordQueueWait`. Progress reports the same wait to the
	client.
- One tool already reports progress: `inventor_session` with `waitSeconds`, through an
	`IProgress<ProgressNotificationValue>` parameter (`InventorTool.Session.cs`, `WaitForAnswerAsync`). The SDK binds
	that parameter to the `progressToken` of the request, and leaves it out of the tool schema.
- 18 call sites in 9 `InventorTool.*.cs` files call `BridgeClient.InvokeAsync`.
- `ToolPipeline` has one call-tool filter for every tool (`AddCallToolFilter` in `ToolPipeline.cs`), with the
	`RequestContext<CallToolRequestParams>` of the call.
- MCP progress: the client puts `_meta.progressToken` on the request, and the server may send
	`notifications/progress` (`progressToken`, `progress`, optional `total` and `message`) until the result. With no
	token, the server must send none. See https://modelcontextprotocol.io/specification/latest/basic/utilities/progress.
- Some clients restart their time limit for a call when progress arrives. That makes a long wait less likely to end
	in a client timeout.

## Decisions

1. **Where progress comes from.** Recommended: one ambient value. The call-tool filter of `ToolPipeline` puts the
	progress sink of the call in an `AsyncLocal`, and `BridgeClient` reads it. The alternative, an `IProgress`
	parameter on each tool method and on `InvokeAsync`, changes 18 call sites and every new tool.
2. **What is reported.**
	- While a call waits at `_gate`: the operation that holds `_gate`, and for how long ("Waiting behind evalCSharp
		for 12 s.").
	- Optional, while the call itself waits for its response: "Waiting for Inventor." Decide after the client
		checks, because the dialog watch already reports a dialog in the result.
	- Only operation names and times. Never a payload, a file name or a document name.
3. **How often.** Recommended: the first report after 2 s of wait, then every 5 s. A short wait gives no report.

## Client checks

Do these before the code. Record the results in `Docs/Research/` (ex. `Progress-In-Clients.md`).

- [ ] Claude Code: does a tool call carry `_meta.progressToken`? Read it in the server log at trace level, or with a
	test tool. Does the client show the message, and does progress restart its time limit for the call?
- [ ] Visual Studio Code: the same.
- [ ] Claude Desktop: the same.

If no client sends a token, stop here and record it. The code would then send nothing.

## Steps

1. `BridgeClient`: record the operation that holds `_gate` and the time it was taken, in two fields that the
	`finally` of `InvokeAsync` clears.
2. `BridgeClient.InvokeAsync`: wait for `_gate` in steps (ex. `WaitAsync(TimeSpan, CancellationToken)`) instead of
	one wait. After each step that did not get `_gate`, report through the ambient sink. Keep
	`RecordQueueWait` for the full wait.
3. `ToolPipeline`: in the call-tool filter, set the ambient sink from the progress token of the request, and clear
	it when the call ends. No token: no sink, and step 2 reports nothing.
4. Tests (`Unit`):
	- A call that waits behind a slow call reports the name of the slow operation, and stops when it gets `_gate`.
	- A call that gets `_gate` at once reports nothing.
	- A call with no progress token reports nothing.
	- A cancel during the wait at `_gate` ends the wait, and no report comes after it.
	- `ToolListIsTheSameAsTheSnapshot` passes with no change: the schemas do not change.

## Definition of done

- [ ] The client checks are recorded.
- [ ] The tests of step 4 pass.
- [ ] Two parallel calls in a client show the report on the second one.
- [ ] `CHANGELOG.md` and `<Version>`: a `Changed` entry. See `.agents/rules/changelog.md`.

## When the plan is done

- Record in `Docs/Architecture.md` that a call that waits reports why, and what each client does with progress.
- Delete this document.
