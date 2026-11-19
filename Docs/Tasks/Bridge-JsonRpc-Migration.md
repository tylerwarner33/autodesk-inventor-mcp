# Bridge JSON-RPC Migration (Bridge Version 3)

Created: 2026-11-14. Plan added: 2026-11-16. Merged with the pipe change: 2026-11-19.

Status: **open. The pipe part is done, the JSON-RPC part is not started. Needs an add-in build for each release.**
The way is now MCP over the pipe. See "Decided for MCP over the pipe".
This document is the plan of the whole bridge version 3 release: the pipe name for each process, the JSON-RPC
envelope, and the add-in part of the document targeting. All of it ships in one pull request, so each user deploys
the add-in one time. The reasons are in `Docs/Tasks/Bridge-Protocol-Decision.md` (condition 1: do the envelope only
in the release that changes the pipe name) and `Docs/Research/Task-Analysis.md`.

## Where the work goes

- **Branch:** `feat/bridge-pipe-per-process`. **Pull request:** #41. Do not open a second pull request.
- Keep #41 open, and do not merge it, until each phase below is done. The pipe change alone is not a release.
- **Version:** protocol version 3 and version 1.0.0, as #41 has them now. Do not use protocol version 4: no add-in
	of protocol version 3 was released.
- **Changelog:** add the JSON-RPC entries to the `[1.0.0]` section that #41 already has. Do not add a new section.
- Merge `master` into the branch before each phase, because #41 stays open for longer than one pull request.
- When the work is done, change the title and the description of #41 to name the whole release, and remove its
	section "Decision for the reviewer".

## What the release contains

| Part | Plan | Status |
| --- | --- | --- |
| The pipe name with protocol version and process id, the first instance flag, the deny entry | `Docs/Tasks/Bridge-Pipe-Options.md` | Done in #41, with its live checks |
| The JSON-RPC envelope, the version detection, the cancellation | This document | Not started |
| The document resolver, `FullDocumentName`, the edit document | `Docs/Tasks/Document-Targeting.md` | Not started. Its live checks come first |
| Removal of the operation `parameters.set` | This document, phase 2 | Not started. `Write-Transactions` is merged (#40), and the server no longer sends it |

The server-only tasks (`Tool-Annotations`, `Tool-Error-Flag`, `Result-Size-Limits`, `Connection-Diagnosis`,
`Write-Transactions`) are merged (#36 to #40).

## JSON-RPC 2.0, the parts that apply

The specification is at https://www.jsonrpc.org/specification. It defines the message shapes only. It does not
define the transport, the framing, or cancellation. MCP uses the same message shapes between the client and the
server (https://modelcontextprotocol.io/specification/latest/basic).

When this release is done, change the second item of "Two protocols are on the two links" in `Docs/Architecture.md`
("Shape") to JSON-RPC 2.0 with the link, and add a row for the bridge to the specification table of `README.md`
("Documentation").

| Message | Members |
| --- | --- |
| Request | `jsonrpc` (exactly `"2.0"`), `method` (a string, not starting with `rpc.`), `params` (optional, an object or an array), `id` (a string or a number) |
| Notification | A request with no `id` member. The receiver must not reply |
| Response | `jsonrpc`, `id` (the value of the request, or null when the id could not be read), and exactly one of `result` and `error` |
| Error object | `code` (an integer), `message` (a string), `data` (optional, any value) |

| Code | Meaning |
| --- | --- |
| -32700 | Parse error: the text is not valid JSON |
| -32600 | Invalid Request |
| -32601 | Method not found |
| -32602 | Invalid params |
| -32603 | Internal error |
| -32000 to -32099 | Server error, for errors that the implementation defines |

A batch is an array of requests. An empty array is an invalid request.

## Mapping from the current protocol

| Now | JSON-RPC 2.0 |
| --- | --- |
| `BridgeRequest.Id` (string) | `id` (keep strings) |
| `BridgeRequest.Operation` (ex. `parameters.evaluate`) | `method` (keep the names) |
| `BridgeRequest.Payload` | `params` (an object, or no member) |
| `BridgeResponse.Success` | No member. `result` or `error` says it |
| `BridgeResponse.Result` | `result` (JSON null when the operation returns nothing, because a response must have one of the two) |
| `BridgeError.Code` (ex. `inventor-busy`) | `error.data.code` |
| `BridgeError.Message` | `error.message` |
| `BridgeError.Detail` | `error.data.detail` |
| Response id `"unknown"` on invalid JSON | `id: null`, code -32700 |
| `unknown-operation` | Code -32601, and `error.data.code` keeps `unknown-operation` |
| A payload that cannot be read (`ParseRequired`) | Code -32602 |
| `internal-error` | Code -32603 |
| Every other `BridgeErrorCodes` value | Code -32000, with the string code in `error.data.code` |

The tool results do not change. `InventorTool.SafeAsync` still returns `{ error, message, detail }` from the
string code, so a model sees no difference.

## Decisions

1. **By hand, or a library.** Decided: by hand. See the decision document.
2. **Batches.** Recommended: the add-in answers an array with -32600. The server sends none.
3. **Cancellation.** Recommended: build it in this release, as the last phase, for work that waits in the queue.
	A later add-in release for it alone costs a new deployment. It is the part to leave out if the release must be
	smaller.
4. **A first message from the add-in (`bridge.hello`).** Decided: none. The pipe name gives the protocol version
	before the connect (done in #41). The reader of the server still accepts a notification, for a later version.

## Decided for MCP over the pipe

The way changed from a hand-written envelope to the MCP SDK on both ends of the pipe
(`Docs/Research/Mcp-Over-Pipe-Spike.md`). The rest of this document is not rewritten for it yet. These details are
decided:

1. **A null result.** One wrapper for every bridge handler changes null to `{}`, because SDK 2.4.0 sends a null
	result with no `result` member and its client then waits forever. The wrapper has a remark that says to remove it
	when the SDK writes a null result, with a pointer to "SDK behavior to check again on each SDK update" in the
	research document. Check that table on each update of the `ModelContextProtocol` packages.
2. **The MCP version.** One constant in `InventorMcp.Contracts` (ex. `BridgeProtocol.McpVersion = "2026-07-28"`)
	sets `McpClientOptions.ProtocolVersion` in the server and `McpServerOptions.ProtocolVersion` in the add-in. No
	older version is supported. An SDK update on one side then cannot change the version on the wire.
3. **A newer MCP version later.** Change it only when a newer version gives the bridge something that it needs, or
	when an SDK release stops supporting the pinned version. Then:
	- Change the constant and `BridgeProtocol.Version` in the same pull request (ex. pipe `v3` to `v4`), so the two
		versions never drift apart.
	- An add-in of the old version then has the old pipe name, and the server gives `bridge-outdated`, as it does
		for version 2 now.
	- The change needs an add-in deployment for each release, and a `Breaking` entry in the changelog.
	- Watch the MCP specification changelog (https://modelcontextprotocol.io/specification/latest) and the SDK release
		notes (https://github.com/modelcontextprotocol/csharp-sdk/releases) for the removal of the pinned version.
4. **One request at a time.** Keep `BridgeClient._gate` in the server. The add-in also runs one request at a time
	for each connection, in the order received, because the SDK runs the requests of one connection in parallel.
	Today the serial read loop of `BridgeServer` gives this, so without the rule a cancelled call and the next call can
	run at the same time, and the next call can run nested in a dialog's modal loop (see the remark in
	`BridgeClient.SendAsync`).
5. **`activity` and `ping` do not wait.** These two operations never use the main thread (the ring buffer of
	`ActivityRecorder` is managed memory). They skip `_gate` in the server and the queue for each connection in the
	add-in, so `inventor_activity` answers while a long call runs. Each operation is marked in one place as "main
	thread" or "no main thread", and a test proves that an operation marked "no main thread" never calls
	`MainThreadDispatcher`. The connect and the reconnect of `BridgeClient` need their own lock, because `_gate` no
	longer guards every use of the connection.

## Rules for the implementation

- Write small records for the envelope in `InventorMcp.Contracts`, serialized with
	`BridgeProtocol.SerializerOptions`. Do not reference the JSON-RPC types of `ModelContextProtocol.Core` from the
	add-in.
- `SerializerOptions` ignores null when it writes. A success response with no result needs `"result": null`
	on the wire. Write the response through a method that adds it, and test that case.
- Keep the framing: one UTF-8 line for each message, no byte order mark, no indentation.
- Keep one request at a time in `BridgeClient`.
- The reader in `BridgeClient` must accept a line with `method` and no `id` at any time, log it, and not take it
	as the response of the waiting request. It must keep the match of a response by its id, and discard a late
	response.
- A new operation after this release does not change `BridgeProtocol.Version`. The server turns -32601 into a
	message that tells the user to deploy the add-in again. The version changes only when an existing message
	changes.
- Do not change the process lock of #41: the resend after a dropped pipe goes to the locked process only. A
	`$/cancelRequest` goes on the connection of the request, never through a new `Resolve`.

## Phase 1 - Contract (`Source/InventorMcp.Contracts`)

- [ ] `JsonRpcMessages.cs`: records `JsonRpcRequest`, `JsonRpcResponse`, `JsonRpcError`, `BridgeErrorData(Code, Detail)`,
	and constants for the codes and for the method `$/cancelRequest`.
- [ ] `BridgeErrorMap`: `ToJsonRpc(BridgeError)` and `FromJsonRpc(JsonRpcError)`, with the mapping table. An
	error with no `data.code` maps to `internal-error`, except -32601, which maps to `unknown-operation`.
- [ ] A function that reads one line into "request", "notification", "response" or "not valid", so both sides use
	one parser.
- [x] Set `BridgeProtocol.Version` to 3, and correct its remark: the pipe name carries the version, and the server
	does not connect to another version. Done in #41.
- [ ] Remove `BridgeRequest`, `BridgeResponse` and `SessionInfo.BridgeVersion` when no code uses them.
- [ ] Unit tests (`Unit/JsonRpcMessagesTests.cs`): `jsonrpc` is `"2.0"`, a response has exactly one of `result` and
	`error`, a null result is written, the id is copied, the id is null on a parse error, a notification has no
	`id` member, and each row of the mapping table in both directions.

## Phase 2 - Add-in (`Source/InventorMcp.AddIn`)

- [ ] `BridgeServer.HandleLineAsync` reads the line with the parser of phase 1 and writes a JSON-RPC response:
	-32700 for invalid JSON, -32600 for an array or an object that is not a request, no response for a
	notification.
- [ ] `OperationDispatcher.DispatchAsync` takes the method, the params and the id, and returns the new response.
	The handlers do not change.
- [ ] Remove `BridgeOperations.SetParameter`, its registration in `InventorOperations.cs`, and
	`InventorOperations.SetParameter` with its request type. No server code sends `parameters.set` since #40.
- [x] Steps 4 to 7 of `Docs/Tasks/Bridge-Pipe-Options.md`. Done in #41.
- [ ] Steps 3 to 7 of `Docs/Tasks/Document-Targeting.md`, after its live checks.

## Phase 3 - Server (`Source/InventorMcp.Server/Bridge`)

- [ ] `BridgeClient.SendAsync` writes a `JsonRpcRequest`. `TakeResponseAsync` reads with the parser, skips a
	notification, and turns an `error` into `InventorBridgeException` through `BridgeErrorMap`.
- [ ] A -32601 error: the message says that the add-in is older than the server, and to deploy it again.
- [x] Steps 8 to 11 of `Docs/Tasks/Bridge-Pipe-Options.md`. Done in #41.
- [ ] Steps 8 to 14 of `Docs/Tasks/Document-Targeting.md`.
- [ ] Tests: change `TestConnection` in `Unit/TestBridge.cs` to the new messages, and keep each test of
	`BridgeClientTests.cs` (a late response is discarded, the dialog watch, the reconnect, the process lock of
	#41). Add: a notification before the response is skipped, an error with `data.code` gives that code, and
	-32601 gives the redeploy message.

## Phase 4 - Cancellation (decision 3)

- [ ] Add-in: `BridgeServer.ServeConnectionAsync` reads the next line while a request runs. Each request gets a
	`CancellationTokenSource` in a table by id, linked to the shutdown token. A `$/cancelRequest` notification
	with `params.id` cancels it. One lock makes each response one whole line. `MainThreadDispatcher.InvokeAsync`
	already drops work that is cancelled before it starts, and `OperationDispatcher` already answers
	`inventor-busy` for it.
- [ ] Add-in: still run one request at a time for each connection, in the order received, so the server's rule
	of one request at a time is also true on the add-in side.
- [ ] Server: `BridgeClient.ReadResponseAsync` sends `$/cancelRequest` with the request id when its
	`cancellationToken` is cancelled, before it throws. Do not send it for `blocked-by-dialog`: the call that
	opened the dialog already runs.
- [ ] Tool descriptions: do not promise that a cancel stops a call. It stops only a call that still waits.

## Tests of both ends together

#41 added `BridgeServer.cs`, `OperationDispatcher.cs` and `RollingFile.cs` to the `Compile Include` list of
`Tests/InventorMcp.Server.Tests/InventorMcp.Server.Tests.csproj`, and `Unit/BridgeEndToEndTests.cs` with the real
`BridgeServer` and the real `BridgeClient`.

- [x] A second `BridgeServer` on the same name fails to start (the first instance flag). Done in #41.
- [x] The listener serves many clients at once, and refuses a client from another computer. Done in #41.
- [ ] Add to `Unit/BridgeEndToEndTests.cs`: a handler that throws `BridgeFailureException`, an unknown method,
	invalid JSON written by a raw client, an array, and 17 clients against 16 instances.
- [ ] With phase 4: a handler that waits, a second request behind it, and a cancel of the second. The second
	handler never runs.

## Phase 5 - Deploy and check

Read `.agents/rules/build.md` and `.agents/rules/addin-isolation.md` first.

- [ ] `dotnet build InventorMcp.slnx`, the unit tests, and the add-in build for 2025, 2026 and 2027.
- [ ] Deploy the add-in for each release, with Inventor closed.
- [ ] Live, for each release: `inventor_session`, `inventor_parameters`, `inventor_eval_csharp` with a helper,
	`inventor_activity`, and a blocking dialog that still gives `blocked-by-dialog`.
- [ ] Live: an add-in of protocol version 2 with the new server gives `bridge-outdated`. #41 did this check, but
	do it again with the final build.
- [ ] Live: run the 3 tests of `LiveInstanceTests` and `EachReleaseAnswersWithItsOwnYearAndProcess` again,
	because the envelope changes each message that they send.
- [ ] Live: the live checks of `Docs/Tasks/Document-Targeting.md`.
- [ ] `CHANGELOG.md`: add the JSON-RPC, cancellation and document targeting entries to the `[1.0.0]` section of
	#41. Keep its `Breaking` entry, which tells users to close Inventor, deploy the add-in for each release again,
	and restart each MCP client. Move the section on top of the newest version on `master`, and set the date of the
	merge. See `.agents/rules/changelog.md`.

## Risks

| Risk | Control |
| --- | --- |
| A session that started before the update keeps its old server, which finds no pipe | Its message already says that Inventor runs and that `inventor_start` must not be called. The changelog entry tells the user to restart the client |
| The match of late responses breaks | Keep each existing test of `BridgeClientTests.cs`. Change only the message types |
| The new read loop of phase 4 writes two responses at the same time | One write lock, and the end-to-end tests |
| An exception in the add-in's connection code ends Inventor | Keep the catch of every exception in `ServeConnectionAsync` |
| #41 stays open for a long time, and `master` moves | Merge `master` into the branch before each phase, and run the unit tests after each merge |
| A user wants the multi-process support before JSON-RPC is ready | Tell the owner. The fallback of the decision document is to ship #41 alone and keep the custom protocol, then delete this document |

## When the plan is done

- Record the protocol, the error mapping, the version rule and the pipe name in `Docs/Architecture.md`. The version
	rule includes the pinned MCP version and item 3 of "Decided for MCP over the pipe": the MCP version and the pipe
	version change together.
- Delete this document, `Docs/Tasks/Bridge-Protocol-Decision.md` and `Docs/Tasks/Bridge-Pipe-Options.md`.
