# MCP Over the Bridge Pipe: Spike Results

Date: 2026-11-19. Branch: `spike/mcp-over-pipe` (from `feat/bridge-pipe-per-process`, not for merge).
Code: `Tests/InventorMcp.Server.Tests/Spike/McpOverPipeSpikeTests.cs` on that branch.

## Question

Can the add-in host an MCP server of the C# SDK (`ModelContextProtocol.Core` 2.4.0) on its named pipe, with the bridge
operations as custom JSON-RPC methods, and can `InventorMcp.Server` use an MCP client of the SDK on the same pipe?
This is the option "MCP over the pipe" for `Docs/Tasks/Bridge-JsonRpc-Migration.md`.

## Setup

- Add-in side: `NamedPipeServerStream` (16 instances, byte mode, asynchronous), one `McpServer.Create` with a
	`StreamServerTransport` for each connection. Each bridge operation is a `McpServerRequestHandler` in
	`McpServerOptions.RequestHandlers`.
- Server side: `NamedPipeClientStream`, `StreamClientTransport`, `McpClient.CreateAsync`, and
	`McpClient.SendRequestAsync(JsonRpcRequest)` for each operation.
- A `SemaphoreSlim(1)` stands in for Inventor's main thread. No Inventor. Windows 11, .NET 10, xunit.v3.
- A raw pipe client wrote JSON lines by hand to see the exact wire text.

## Results

| Check | Result |
| --- | --- |
| Custom method round trip | Works. Method names such as `parameters.evaluate` stay as they are. Connect and `initialize` 90 ms, one call 3 ms |
| Negotiated MCP version | `2026-07-28` with the SDK client. A raw client that asks for `2025-06-18` gets `2025-06-18` |
| Bridge string code | `McpProtocolException(message, (McpErrorCode)(-32000))` with `exception.Data["code"] = "inventor-busy"` gives `{"code":-32000,"message":"Inventor is busy.","data":{"code":"inventor-busy"}}`. The SDK client gets the code and `Data["code"]` back |
| Other exceptions | `InvalidOperationException`: `-32603`, message replaced by `"An error occurred."`. `McpException`: `-32603`, message kept |
| Unknown method | `-32601`, `"Method 'x' is not available."` |
| Invalid JSON on the pipe | No answer, and the session goes on: the next request gets its response |
| **A handler that returns null** | **The response has no `result` and no `error`** (`{"id":"x","jsonrpc":"2.0"}`), which JSON-RPC 2.0 does not allow. **The SDK client never completes the call.** `JsonValue.Create((string?)null)` gives the same. Return an empty object instead |
| Concurrency | The SDK runs two requests of one connection at the same time. The add-in must keep its own queue to the main thread (`MainThreadDispatcher` does) |
| Cancel of a running handler | The handler's token is cancelled 4 ms after the client cancels. The session serves the next call |
| Cancel of a call that waits for the main thread | Run alone, 8 of 8: the body never runs. With the other spike tests in parallel, 6 of 8: the body runs, because `notifications/cancelled` reaches the add-in after the main thread is free. The client already reports the call as cancelled |
| `initialize` while the main thread is held | 90 ms. The handshake does not need the main thread. A call that needs it waits, and runs after the release |
| Dropped pipe during a call | `IOException` "The server shut down unexpectedly." after 1 ms |
| 4 clients on one listener | Works |
| 8 MB result | 105 ms |
| `ping` | Not available on `2026-07-28` (`"The method 'ping' is not available on protocol version '2026-07-28'"`). It works on `2025-06-18` |
| `McpServerRequestHandler` | `[Experimental("MCPEXP002")]`. The build needs a suppression |

## Add-in footprint on .NET 8

A `net8.0-windows` library with `ModelContextProtocol.Core` 2.4.0 copies 9 assemblies, about 3.1 MB:
`ModelContextProtocol.Core` (1278 KB), `Microsoft.Extensions.AI.Abstractions` (665 KB), `System.Text.Json` (660 KB),
`System.Diagnostics.DiagnosticSource` (187 KB), `System.IO.Pipelines`, `Microsoft.Extensions.Logging.Abstractions`,
`Microsoft.Extensions.DependencyInjection.Abstractions`, `System.Text.Encodings.Web`, `System.Net.ServerSentEvents`.
On .NET 8, `System.Text.Json` and `System.Diagnostics.DiagnosticSource` version 10 replace the framework versions in
the add-in's isolated context. The 2025 add-in ships 9 assemblies today, most of them Roslyn.

## Live results in Inventor

Date: 2026-11-19. The spike branch adds `McpSpikeHost` to the add-in: an SDK `McpServer` on a second pipe,
`InventorMcp.Spike.<year>.<processId>`, beside the real bridge. `spike.info` reports the load contexts and needs no
main thread. `spike.mainthread` reads the application through `MainThreadDispatcher`. A console probe with
`McpClient` connected as soon as the pipe was there. Then the live tests (`LiveTransactionTests`,
`LiveInstanceTests`, `LiveDialogTests`) ran against the same add-in.

| | 2025.4 | 2026.4 | 2027.2 |
| --- | --- | --- | --- |
| Runtime in Inventor | .NET 8.0.34 | .NET 10.0.15 (the add-in targets net8.0) | .NET 10.0.15 |
| Add-in context | `InventorMcp.AddIn` (loader) | `InventorMcp.AddIn` (loader) | Inventor's `AddinLoadContext` |
| `System.Text.Json` of the add-in | 10.0 from `App\`. The default context has 8.0 | 10.0 from `App\`. The default context has the framework 10.0 | The framework 10.0 in the default context. Not copied |
| Handshake during startup (`Ready` false) | 229 ms | 196 ms | 145 ms |
| First main thread call during startup | One 2 s timeout, then 346 ms | One 2 s timeout, then 106 ms | One 2 s timeout, then 1914 ms |
| Warm: handshake, 20 main thread calls | 104 ms, 13 ms | 98 ms, 6 ms | 99 ms, 9 ms |
| Live tests (19) | 15 passed, 4 skipped, 0 failed | 15 passed, 4 skipped, 0 failed | 15 passed, 4 skipped, 0 failed |
| Roslyn after a rule run | 4.6 default, 4.14 add-in | 4.13 default, 4.14 add-in | 4.13 default, 4.14 add-in |

- On 2027, another add-in loads `ModelContextProtocol.Core` 1.2.0 and `Microsoft.Extensions.AI.Abstractions` 10.4
	in its own `AddinLoadContext`. The add-in's 2.4.0 loads beside it with no conflict.
- On 2025, `System.Text.Json` 8 (default) and 10 (add-in) are in the process at the same time, and the snippets,
	the rules and the dialog handling of the live tests all work.
- The SDK adds `_meta` with `io.modelcontextprotocol/serverInfo` to each custom result on `2026-07-28`. A reader of
	a bridge result must ignore members that it does not know.
- The handshake answers while the main thread is busy at startup, on all three releases. A Vault login dialog was
	not seen to stay open during these runs, so a handshake under a real modal dialog is still not measured.
- The add-in log shows a clean start and a clean stop of the host on each release.

## SDK behavior to check again on each SDK update

The add-in works around these. When the SDK changes, use its default behavior and remove the workaround.

| Behavior in 2.4.0 | Workaround in the add-in | How to check a new SDK | When fixed |
| --- | --- | --- | --- |
| A handler that returns null gives a response with no `result` and no `error`, and the SDK client never completes the call. Cause: `McpJsonUtilities` writes no null property (`WhenWritingNull`), and `JsonRpcResponse.Result` has no exception to it. Not reported upstream on 2026-11-19 | One wrapper for every bridge handler changes null to `{}` | `TheWireShowsWhatANullResultGives` of the spike: the line has `"result"` | Remove the wrapper. Keep a test that a handler that returns null completes the call |
| `McpServerRequestHandler` is `[Experimental("MCPEXP002")]` | One suppression with its reason | The attribute is gone, or the API changed | Remove the suppression, or change the handlers |
| On `2026-07-28` the SDK adds `_meta` (`io.modelcontextprotocol/serverInfo`) to each custom result | The reader of a result ignores members that it does not know | Not a defect. Check only that the reader still ignores it | Nothing to remove |

The specifications: JSON-RPC 2.0 (https://www.jsonrpc.org/specification#response_object, "Either the result member
or error member MUST be included") and the MCP schema
(https://github.com/modelcontextprotocol/modelcontextprotocol/tree/main/schema, where `result` is an object).

## The MCP version of the bridge

The SDK settings, from its source (2.4.0):

- `McpClientOptions.ProtocolVersion` set: the client asks for exactly that version, and it is also the minimum. If
	the server cannot give it, the client throws `McpException` and does not fall back.
- `McpServerOptions.ProtocolVersion` set to `2026-07-28`: the server rejects `initialize`, so it refuses every
	older version.
- Not set: each side accepts the 5 versions that the SDK knows (`2024-11-05` to `2026-07-28`), so the result
	depends on the two SDK versions that meet.
- `2026-07-28` has no `initialize`. The client sends `server/discover`, and each request carries the version in
	its `_meta`. So each side checks the version on each request, and the setting is needed on both sides.

## What the results mean for the plan

- The option works for each check that does not need Inventor.
- A bridge handler must never return null. Wrap each result, or return an empty object.
- A cancel is a request, not a guarantee. A write that the client cancelled can still run. A tool must not report
	"not done" after a cancel: read the model back, as for a dropped pipe.
- Pin `ProtocolVersion` on both sides (`McpServerOptions`, `McpClientOptions`). Otherwise an add-in and a server built
	on different SDK versions negotiate a version, and a method such as `ping` can appear or go away.
- The custom handler API is experimental, so an SDK update can change it. The add-in is not rebuilt for each SDK
	update, but a rebuild after such a change needs code work.
- The SDK assemblies load in Inventor 2025, 2026 and 2027, and the existing live tests still pass.
- Not checked: a handshake while a real modal dialog is open, and the use of `McpClient` under the existing
	`BridgeClient` rules (one request at a time, the dialog watch, the process lock of #41, the host check).
- The null result defect of the SDK is described for upstream in a task of the SDK clone, not in this repository.
