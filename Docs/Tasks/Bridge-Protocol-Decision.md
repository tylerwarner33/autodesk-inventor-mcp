# Bridge Protocol Decision

Created: 2026-11-14. Review added: 2026-11-16.

Status: **decided: change to JSON-RPC 2.0, written by hand. Reviewed, with two conditions.** The plan is in
`Docs/Tasks/Bridge-JsonRpc-Migration.md`. The full review is in `Docs/Research/Task-Analysis.md`, "Bridge
protocol decision and JSON-RPC migration". This document keeps the options and the reasons until the decision is
recorded in `Docs/Architecture.md`.

The pipe between `InventorMcp.Server` and `InventorMcp.AddIn` carries a protocol of this repository:
newline-delimited JSON, `BridgeRequest(Id, Operation, Payload)` and `BridgeResponse(Id, Success, Result, Error)`,
with string error codes in `BridgeErrorCodes`.

## What the decision is about

MCP itself is JSON-RPC 2.0 (https://www.jsonrpc.org/specification, and the MCP base protocol at
https://modelcontextprotocol.io/specification/latest/basic), and the server already speaks it to the client through
the C# MCP SDK. This decision
is only about the internal bridge, which has one client (the server) and one host (the add-in), both in this
repository.

## Options

1. **Keep the custom protocol.**
	- For: typed records in one shared assembly, no dependency in the add-in, string error codes that a model can read.
	- Against: a new need (ex. a cancellation) needs a design of this repository.
2. **Use the JSON-RPC 2.0 envelope, written by hand.** Selected.
	- For: a known specification for notifications and for errors. The string code moves to `error.data`.
	- Against: a protocol version change and an add-in build for each release, with no new feature by itself.
3. **Use a JSON-RPC library on both sides.**
	- Against: StreamJsonRpc has seven direct dependencies (ex. MessagePack, Newtonsoft.Json), and the JSON-RPC
		types of `ModelContextProtocol.Core` bring four assemblies on net8.0. Each must load in the add-in's
		isolated context (see `.agents/rules/addin-isolation.md`).
4. **Send MCP itself over the pipe.** Not a real option: it reverses "Two processes rather than one".

## Review of the gains

| Gain in the first decision | Review |
| --- | --- |
| A known specification | True. The current protocol already has the same shape, so the gain is the member names |
| The add-in can push events (ex. a dialog opened) | Removed. It goes against "The activity feed is a pull, not a push", and the add-in cannot see a dialog while its main thread is in the modal loop |
| A first message after the connect | Removed. The pipe name carries the protocol version (see `Docs/Tasks/Bridge-Pipe-Options.md`) |
| A cancellation drops work that waits in the queue | Real, and the only new behavior. It needs a change of the add-in's read loop, not only the envelope |
| Any JSON-RPC tool can read the messages | True, small |

## Verdict

- **Makes sense:** partly. The value of the envelope alone is low.
- **Recommended:** yes, under two conditions.
- **Architecture:** agrees with "The add-in stays thin" and the isolation rule, because no library is used.

Conditions:

1. **Do it only in the release that changes the pipe name.** That release needs a new deployment of the add-in in
	any case, so the envelope adds no deployment.
2. **Keep the activity feed a pull.** Notifications are used for the cancellation only.

The alternative, if the release must be smaller: keep option 1 and do only the pipe change. The version
detection, which is the real defect of today, comes from the pipe name in both cases.

## Phases

- [x] Choose an option.
- [x] Review the option against the code and the architecture.
- [ ] Do `Docs/Tasks/Bridge-JsonRpc-Migration.md`.

## When the plan is done

- `Docs/Tasks/Bridge-JsonRpc-Migration.md` records the decision in `Docs/Architecture.md` and deletes this
	document.
