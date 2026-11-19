# Task Analysis

Created: 2026-11-16

The analysis behind the implementation plans in `Docs/Tasks/`. Each plan there is short and gives the steps. This
document gives the reasons, the facts that were checked, and the options that were not selected. A task document
is deleted when its work is done. This document stays.

## How the analysis was done

- **The code.** Each file that a task names was read: the bridge on both sides, the contracts, the tool classes,
	the script prelude, and the tests.
- **The C# MCP SDK.** The XML documentation of `ModelContextProtocol` 2.0.0 in the NuGet cache, and the SDK source
	at the commit of the 2.0.0 release. No tool was run to confirm the behavior. The plans say where a check is
	necessary.
- **The MCP specification.** Revision 2026-07-28, the pages for tools and the schema.
- **Named pipes.** The `dotnet/runtime` source of `System.IO.Pipes` (main and release/8.0), the Win32
	documentation, and two probes with temporary pipes on Windows 11 and .NET 10. A fact from a probe is marked
	"observed".
- **The Inventor API.** `inventor_api_lookup` for each member that a plan uses.
- **The usage.** The audit logs (`executed-code.log`) of this machine. Only counts are recorded here.

No live Inventor session was available. Thus each fact about the behavior of Inventor (ex. a transaction that
contains a rule) is not measured, and its plan starts with a live check.

## Summary

| Task | Makes sense | Recommended | Fit with `Docs/Architecture.md` |
| --- | --- | --- | --- |
| Tool annotations | Yes | Yes, first | Agrees. One row of the proposed table goes against a code decision |
| Tool error flag | Yes | Yes | Agrees. The place of the decision changes: the call tool filter, not `SafeAsync` |
| Result size limits | Yes | Yes | Agrees. The audit log does not keep the output, so the plan adds a file |
| Connection diagnosis | Yes | Yes | Agrees. Server only |
| Write transactions | Yes, but the scope is small | Yes, after the live checks | Agrees. Server only |
| Document targeting | Yes | Yes | The single resolver is the add-in. This removes server code, and needs an add-in build |
| Bridge pipe options | Yes | Yes | Changes one decision: a pipe for each process, which was "not selected" |
| Bridge protocol decision | Partly | Acceptable, low value alone | Agrees, if no library is used and the activity feed stays a pull |
| Bridge JSON-RPC migration | Partly | Only together with the pipe change | Same as the row above |
| Dedicated tool candidates | 2 of 14 rows | The selection tool and the save tool | The other rows go against "Execution rather than a tool per operation" |
| Skills over MCP | Yes | Wait. The SDK package is released, no client uses it | Agrees. Two sentences in the architecture document are out of date |
| Live verification | Yes | Continue. Three items change | Not a feature |

## Order of the work

The largest cost in this repository is an add-in build, because each release needs Inventor closed and a new
deployment. So the order puts the server-only work first, and puts each add-in change into one release.

1. **Tool annotations.** Attributes and one test. No risk.
2. **Tool error flag.** One filter and one pure function.
3. **Result size limits.** One helper. It uses the error flag for its smaller limit.
4. **Connection diagnosis.** Server only. It also catches a connect that the ACL refuses, which the pipe change
	makes more visible.
5. **Write transactions.** Server only: a prelude helper and the parameter snippet. Starts with live checks.
6. **Bridge version 3.** One add-in release with: the pipe name with protocol version and process id, the first
	instance flag, the deny entry for remote clients, the JSON-RPC envelope, and the add-in part of document
	targeting. One new deployment covers all of them.
7. **Dedicated tools.** The selection tool after document targeting, because both use the edit document.
8. **Skills over MCP.** When a client that the team uses supports the extension.

Steps 1 to 5 are independent. Each can be one pull request.

## Tool annotations

**What the code has.** 35 tools. 18 have no hint. 2 have every hint (`inventor_start`, `inventor_use_release`).
The others have 1 or 2 hints.

**What the SDK does (2.0.0 source, `McpServerToolAttribute` and `DeriveOptions`).** Each hint has a nullable
backing field. Only a hint that the attribute sets is copied. With no hint and no title, `ProtocolTool.Annotations`
is null. So `ReadOnly = false`, written explicitly, is not the same as no value: it gives `readOnlyHint: false`
on the wire. A test can read `McpServerTool.Create(method).ProtocolTool.Annotations`.

**What the specification says (2026-07-28).** Defaults: `readOnlyHint` false, `destructiveHint` true,
`idempotentHint` false, `openWorldHint` true. `destructiveHint` and `idempotentHint` have a meaning only when
`readOnlyHint` is false. A client must consider the hints untrusted unless the server is trusted.

**What clients do.**

| Client | Use of the hints | Source |
| --- | --- | --- |
| Claude Code | Runs a call of a `readOnlyHint` tool in parallel with other read-only calls. Can repeat an `idempotentHint` call after a closed connection. No documented effect on permission prompts | Claude Code documentation, MCP page |
| Visual Studio Code | `readOnlyHint` changes the default confirmation before a call, and `openWorldHint` the confirmation of results. The documentation says not to assume that a hint skips approval | Visual Studio Code MCP developer guide |
| ChatGPT | Requires `readOnlyHint`, `destructiveHint` and `openWorldHint` on each tool | Apps SDK reference |
| Claude Desktop, Visual Studio, Cursor | Not verified | |

So the task is correct that missing hints make a read tool look destructive and open-world. Its statement that
the hints change auto-approval is true for Visual Studio Code and not documented for Claude Code. The parallel
calls of Claude Code are safe here, because `BridgeClient` sends one request at a time.

**Corrections to the proposed table.**

- `inventor_ilogic_rule_get` must not become `ReadOnly`. The comment above it in `InventorTool.ILogic.cs` records
	the decision: with `outputFolder` it writes files, and a client can run a read-only tool with no approval.
	It keeps `ReadOnly = false`, `Destructive = false`, and gets `Idempotent = true` and `OpenWorld = false`.
- Decision 2 of the task (`inventor_export_sheet_image` writes a file) has no object. The tool writes a temporary
	file, reads it, and deletes it in a `finally` block. The result is image content. It stays `ReadOnly`.
- The tools that read a file by path (`inventor_file_info`, `inventor_features`, `inventor_drawing_layout`) open
	the document invisibly with the rules off and close it with no save. That agrees with "does not modify its
	environment".

**Decision 1 of the task (idempotent parameter writes).** Agreed: `Idempotent = false`, because a write can run
iLogic rules again.

**Risk.** None for the model. A client can ask the user less often for a read tool, which is the purpose.

## Tool error flag

**What the code has.** `SafeAsync` catches `InventorBridgeException` and returns `{ error, message, detail }`.
But about 40 other places return an `error` object with no exception: argument checks before `SafeAsync`
(`invalid-arguments`), results inside a `SafeAsync` lambda (`anchor-not-unique`, `rule-changed`,
`target-exists`), `UnlessBlockedAsync`, every result of `inventor_start`, `inventor_skill`, and
`RunJsonSnippetAsync`, which turns a snippet that threw into an `execution-failed` element. A snippet that threw
in `inventor_eval_csharp` has no `error` member at all: it is an `ExecutionResult` with `succeeded: false`. The
result can also be wrapped: `{ result, blockingDialogs }` or `{ result, rulesSuppressed }`.

So the rule of the task, "one place decides: `SafeAsync`", cannot work. `SafeAsync` sees less than half of the
failures.

**The place that sees every result.** `Program.cs` already has a call tool filter (`AddCallToolFilter`). It
gets the `CallToolResult` of each tool, with the tool name. A pure function there can read the JSON text of the
first text block and decide. No tool changes, and the JSON body stays the same.

**What the SDK does (2.0.0, `AIFunctionMcpServerTool.InvokeAsync`).** The conversion is chosen from the run-time
value. An object becomes one text block with JSON. A `CallToolResult` is returned with no change.
`UseStructuredContent` is false by default, so no structured content is written. An exception that is not a
protocol exception becomes `isError: true` with the text "An error occurred invoking '<name>'." and no message,
which confirms the remark on `SafeAsync` about opaque errors.

**What the specification says.** A tool execution error is a result with `isError: true`, and "Clients SHOULD
provide tool execution errors to language models to enable self-correction". Input validation errors and
business logic errors are named as examples. So `not-found`, `invalid-arguments` and a snippet that does not
compile are exactly this kind.

**The rule.** A result is an error when its top-level object has a string member `error`, or has
`succeeded: false`. A wrapper with a `result` object is examined one level down. A small table of
(tool, code) pairs is exempt, because the code is the answer to the question:

| Tool | Code | Why it is not an error |
| --- | --- | --- |
| `inventor_session` | `inventor-not-running` | The tool reports if Inventor can be reached |
| `inventor_dialogs` | `inventor-not-running` | There is no dialog to read |
| `inventor_start` | `inventor-starting-or-no-bridge`, `version-required` | The next step is a wait or a question to the user |

**Partial batches.** `inventor_set_parameters` returns one result for each parameter. A flag on a partial result
invites the model to repeat the whole batch, and a parameter write is not idempotent. So a batch is an error only
when no item succeeded. The tool then adds a top-level `error`, and the filter needs no special case.

**A limit of Claude Code.** For a result with `isError: true`, Claude Code keeps only the first 5,000 and the
last 5,000 characters when the text is longer than about 11,000 characters. A compile failure with many
diagnostics and `Log` lines can be longer. So the result size task uses a smaller limit for a failed result.

**Risk.** A client can show a failed call in a different way. The model reads the same JSON. No instruction or
skill names `isError`, so no text changes.

## Result size limits

**What the code has.** `SafeAsync` passes each `ExecutionResult` through `Complete`. That is one place for a
limit. `inventor_run_ilogic` returns no `Log` output and no return value (`RunTemporaryRule` gives empty lists),
so the limit has an effect only on `inventor_eval_csharp` today. `RunJsonSnippetAsync` parses the return value as
JSON, so the limit must not apply inside it.

**The audit log does not keep the output.** The task says "the audit log keeps the full text". It keeps the
snippet and one result line (succeeded or failed, and the time). The `Log` lines and the return value are not
written. So text that the limit drops is lost, and to get it again the model must run the snippet again. For a
snippet that writes to the model, a second run is a second write. So the plan saves the full text to a file and
names the file in the result.

**Client limits.**

| Client | Limit for one result | Source |
| --- | --- | --- |
| Claude Code | A warning above 10,000 tokens. Above 25,000 tokens (`MAX_MCP_OUTPUT_TOKENS`) or above 50,000 characters of text, the result is saved to a file and the model gets the path. The result is not refused | Claude Code documentation, "MCP output limits and warnings" |
| Claude Code, error results | About 11,000 characters, then first 5,000 and last 5,000 | Same page |
| Visual Studio Code, Claude Desktop, Visual Studio, Cursor | Not verified. No official number was found | |

The research document counted two results "over the token limit": 106,000 characters and 50,000 characters. The
second number is the 50,000 character threshold of Claude Code.

**The numbers.** 40,000 characters for a result that succeeded: below the 50,000 of Claude Code, and about
10,000 to 16,000 tokens. 10,000 characters for a result that failed. `MaxTreeCharacters` for
`inventor_assembly_tree` is 60,000, which is above the threshold of Claude Code, so the plan sets it to the same
40,000.

**Start, or start and end.** Start and end. A snippet that reports progress writes its summary in the last
lines. The cut is at a line end where possible, with one marker line in the middle that gives the number of lines
and characters left out, and the file.

## Connection diagnosis

**What the code has.** `EnsureConnectedAsync` catches `TimeoutException` and `OperationCanceledException` from
`ConnectAsync`. `NotRunningMessage` then gives one text for three causes when an Inventor of the release runs.

**Facts (runtime source and observed).**

- A pipe with all instances in use: `ConnectAsync` waits and throws `TimeoutException` at the timeout.
- No such pipe: the same `TimeoutException`.
- A pipe whose ACL refuses the account: `UnauthorizedAccessException` at once, with no retry. The code does not
	catch it today, so the tool call ends as an opaque SDK error.
- `Directory.EnumerateFiles` on the pipe directory uses no instance. `File.Exists` on a pipe name connects to an
	instance and uses it. Never probe with `File.Exists`.

**What the server can tell with no bridge call.** The pipe list, the Inventor processes of the release with
their start time and main window (`InventorInstallations.FindRunning`), and if the manifest of the release is
deployed (`InventorInstallations.FindInstalled`). The start time separates "still loading" from "not loaded",
which the task did not have: an Inventor that started less than 120 s ago is probably loading.

**The code for "all connections in use".** A new code is better. A wait cannot free a connection, and the logs
can then count the case. `inventor_session` with `waitSeconds` already stops its wait on a code other than
`inventor-not-running`. The other causes keep `inventor-not-running`, because the two wait loops poll on it.

**Pipe drops.** The process id must be read before `CloseAsync`, which clears it. The task says not to change the
retry. Agreed, with one note for the bridge version 3 work: after a drop, the retry sends the same request to
whatever process now hosts the pipe. With the process id in the pipe name, a session keeps its process, so the
retry cannot reach a different Inventor.

## Write transactions

**What the code has.** Only `inventor_auto_balloon` starts a transaction. The tools that change a model through
a composed snippet are few: `inventor_set_parameters`, and `inventor_set_parameter` with `suppressRules`. The
other write tools either make one change (`inventor_set_property`, `inventor_update`), write no model data
(`inventor_ilogic_rule_set`, which can also save), or close documents. So the scope is smaller than "each write
tool": it is the parameter writes, plus a helper for snippets.

**The API (`inventor_api_lookup`).** `TransactionManager.StartTransaction(Document, String)`,
`Transaction.End`, `Transaction.Abort`. `End` and `Abort` fail when the transaction is not the current one.
`inventor_auto_balloon` casts the document to `_Document` for the call.

**Decision 1, a batch with some failures.** Keep the successes. The result already has one entry for each
parameter, and the pattern in `Dedicated-Tool-Candidates.md` tells the model to repeat only the failed items. An
atomic batch would go against that pattern. An exception outside the loop (ex. from `Update`) aborts the
transaction, so the document goes back to its state before the call.

**Decision 2, the bridge operations.** Send `inventor_set_parameter` through the composed snippet in every case.
This follows "Compose server-side tools before adding bridge operations", needs no add-in build, and makes one
write path. The snippet must then do what the add-in operation does: validate a numeric expression with
`IsExpressionValid`, and return the kind, the internal value and the display value. The operation
`parameters.set` then has no caller, and bridge version 3 removes it. `inventor_set_property` keeps its
operation: it is one change.

**Decision 3, snippets of the model.** No `transactionName` parameter. A wrapper around the code of the model
breaks a snippet that declares a method or returns at the top level (the remark on `WithRulesSuppressedAsync`
records this). A prelude helper that the model calls by choice has no such problem and no cost for other
snippets: `InTransaction(document, name, action)`.

**What is not known.** If a rule runs correctly inside a transaction, what a save or an open does inside one, and
if a nested transaction is merged. The API documentation does not say. The plan gives the branch for each
result.

**Tests.** A unit test can only examine the text of a composed snippet. The real check is a test of the `Live`
level.

## Document targeting

**What the code has.** Four resolvers, not two:

| Where | Rule |
| --- | --- |
| `ResolveDocument` in the add-in | Empty: `ActiveDocument`. Else the first document with that display name or full file name |
| `FindToolDocument` in the composed snippets | The same rule again, plus: open the file when the name is a path of a file that is not open |
| `SessionStateSnippet` | A filter on the display name or the full file name |
| `inventor_auto_balloon` and `inventor_drawing_layout` | `Application.ActiveDocument`, or a loop on the full file name |

`OpenOrReuseDocument` in the prelude also returns the first document with the full file name, so it has gap 2 too.

**The API (`inventor_api_lookup`).** `Application.ActiveEditDocument`: "the current in-place editing document".
`Document.FullDocumentName`: "the full file name concatenated with the document name and is a unique identifier
for the document". `FileManager.GetFullDocumentName(file, modelState)` builds one. The audit logs have 358
snippets with `ActiveDocument`, 95 with `FullDocumentName`, and none with `ActiveEditDocument`.

**One resolver, and where.** The task asks for "the same rules in the add-in and in the snippets, from one
source". The options:

| Option | Result |
| --- | --- |
| One source file, compiled into the add-in and sent as prelude text | Two compilers for one file. The add-in throws a bridge error with a code, a snippet can only throw an exception |
| A helper on the script globals | Goes against "Script helpers travel with the snippet" |
| The server resolves first, then sends an exact name | One more round trip for each call |
| **The add-in resolves, and the snippets do not** | Selected |

Each snippet already goes through the add-in, and `ExecuteRequest.DocumentName` is already resolved there by
`ResolveDocument` to set the `Document` global. Today `RunJsonSnippetAsync` sends no document name and lets the
snippet find the document again. When it sends the name, the add-in resolver is the only one, its errors are real
bridge errors with codes (`not-found`, `ambiguous-document`, `no-active-document`), and the snippet uses
`Document`. The cost: a change of the rules needs an add-in build. The rules are four lines and become stable
fast, as the bridge did.

The match itself is a pure function on three strings for each document. It has no Inventor type, so the test
project can compile the file, as it does for `ReturnValueText.cs`.

**Decision 1, the default document.** The edit document. It is what the user sees and means with "this part".
`inventor_session` reports both documents, and each tool description says which one an empty name gives. A tool
that needs an assembly then returns `wrong-document-type` during an edit of a part in place, and the message
names the part, so the model sends the assembly name.

**Decision 2, a path that matches two model states.** An error with the full document name of each match. The
first match of today is arbitrary. One more call occurs only in this case.

**The file that is not open.** The server tries the call with the name. On `not-found`, when the tool can open a
file and the name is a path that exists, it sends the snippet again with a preamble that opens the file with the
rules off. The first try costs some milliseconds, which is small against the time to open a document.

**Sequence.** The resolver is add-in code, so it goes into the bridge version 3 release.

## Bridge pipe options

**Open question 1 is answered.** Two processes of one user can serve one pipe name (observed with two .NET
processes, the same call as `BridgeServer.CreatePipe`).

- The second process creates instances with no error, because the ACL gives the user `CreateNewInstance`.
	Only a different direction made the create fail. A different type or a different maximum did not.
- A client gets the instance that has listened for the longest time, across both processes. So the servers of
	one machine are spread over both Inventor processes, in an order that nobody controls.
- `GetNamedPipeServerProcessId` returned the process that created the first instance, also for a client that was
	connected to the instance of the second process. So today, with two Inventor processes of one release, the
	server can check the wrong process in `PipeHost.Check`, and read and click the dialogs of the wrong Inventor.
- The `UnauthorizedAccessException` branch in `AcceptLoopAsync` ("Could not claim the pipe") occurs only when
	the ACL refuses the create, which is the case of a different Windows user.

So `README.md` is correct ("compete for one pipe"), and `Docs/Architecture.md`, `Docs/Setup-and-Usage-Guide.md`
and the remark on `BridgeProtocol.PipeNameFor` are wrong. The two processes do not only compete: a session can
change its Inventor after a reconnect, and a retried write can go to the other process.

**Decision 1 and 2, the name.** A pipe for each process: `InventorMcp.Bridge.v<protocol>.<year>.<process id>`.

| Need | Per release with the first instance flag | Per process |
| --- | --- | --- |
| A second Inventor of the release | Has no bridge | Has its own bridge |
| A second Windows user | Has no bridge | Has its own bridge |
| The process id is correct | Yes, one process only | Yes, and the name gives a second check |
| A session keeps its Inventor | Yes | Yes, by the name |
| Server work | None | A choice between processes |
| Add-in work | One flag | One flag and the name |

`Docs/Architecture.md` did not select this because "it needs a discovery step". That cost is paid already: since
protocol version 2 the server lists the pipes to find the releases. The add-in part is the same size in both
columns, and only the add-in part needs a new deployment. The server part (a choice when two processes run) can
start simple and change later with no add-in build.

`PipeOptions.FirstPipeInstance` (new in .NET 8, accepted by `NamedPipeServerStreamAcl.Create`) goes on the first
instance in both cases. It makes the create fail when the name exists. It protects only the call that has it.

**The protocol version in the name.** The server then knows the version of each add-in from the pipe list, before
it connects and with no message. A request at connect time is not possible, because a dialog at startup can
block it (see "The server waits for Inventor, not the model"). So the first message of the JSON-RPC task
(`bridge.hello`) is not necessary: the name gives the version and the release, and the pipe gives the process.

**Decision 3, remote clients.** Add a deny entry for the `NETWORK` SID (S-1-5-2). .NET never sets
`PIPE_REJECT_REMOTE_CLIENTS`, and `PipeOptions.CurrentUserOnly` cannot be used together with an explicit
`PipeSecurity` (the code throws `ArgumentException`). WCF uses the same deny entry. A remote client needs the
credentials of the user, so this is a second barrier, not the first. It costs one line.

**Not changed.** 16 instances, one duplex pipe, byte mode, one message for each line. `PipeOptions.CurrentUserOnly`
on the client would only repeat the owner check of `PipeHost.Check`, and for an elevated process it compares
against the owner of the token, which can be the administrators group.

## Bridge protocol decision and JSON-RPC migration

**A second look at the decision.** The decision document lists four gains. Each one, examined:

| Gain in the decision | Assessment |
| --- | --- |
| A known specification in place of a design of this repository | True. The current protocol already has the same shape, so the gain is the names of the members |
| Notifications let the add-in push events (ex. a dialog opened) | Weak. `Docs/Architecture.md` decides "The activity feed is a pull, not a push". The add-in cannot see a dialog while its main thread is in the modal loop, which is why the server watches from outside |
| A first message after the connect | Not necessary when the pipe name has the protocol version |
| A cancellation notification drops work that waits in the queue | Real. Today a call that the user cancels while it waits behind a long call still runs later. But it needs more than the envelope: the add-in reads the next line only after the current request ends, so it cannot receive a cancel during a request |
| Any JSON-RPC tool can read the messages | True, small |

The costs are a new protocol version, a new deployment for each release, and a change to a path that works and
has tests (the match of a late response in `BridgeClient`).

**Verdict.** The change is acceptable and has low value by itself. It is reasonable only in the same release as
the pipe change, which needs a new deployment in any case. It does not go against the architecture when:

- The envelope is written by hand. `ModelContextProtocol.Core` has public JSON-RPC types, but it brings
	`Microsoft.Extensions.AI.Abstractions` and three more assemblies into the load context of the add-in on
	net8.0. StreamJsonRpc brings seven direct dependencies, with MessagePack and Newtonsoft.Json.
- The activity feed stays a pull.

If the owner prefers the smallest change, the alternative is: keep the envelope, change the pipe name and the
flags, and add nothing else. The version detection, which is the real defect, comes from the pipe name in both
cases.

**The version detection defect.** No code compares `SessionInfo.BridgeVersion` with `BridgeProtocol.Version`,
and `version-mismatch` has no use. The remark on `BridgeProtocol.Version` says that the server refuses a bridge
of another version, which is not true.

**Decisions of the migration task.**

1. By hand. Agreed.
2. Batches: answer an array with -32600. The server sends none.
3. Cancellation: build it in this release, as the last step, limited to work that waits in the queue. The
	reason is the cost of a later add-in release for it. It is the part to leave out if the release must be
	smaller.

**Compatible changes after version 3.** A new operation does not change the version. An older add-in answers
-32601, and the server turns it into a message that tells the user to deploy the add-in again. The version
changes only when an existing message changes.

**An old server with a new add-in.** A session that started before the update keeps its old server, which looks
for the old pipe name. It reports `inventor-not-running`, and its message already says that Inventor runs and
that `inventor_start` must not be called. A restart of the client corrects it. A second pipe with the old name
that answers "restart the client" was considered and not selected: more add-in code for a case that ends with
the next restart.

**A test that the code allows.** `BridgeServer.cs` and `OperationDispatcher.cs` use no Inventor type. The test
project can compile them, as it does for `LogThrottle.cs`, and run the real add-in listener against the real
`BridgeClient` with test handlers. Today the tests use a fake listener, so the two ends were never tested
together.

## Dedicated tool candidates

**The test from the architecture.** "Execution rather than a tool per operation": a tool for each operation is a
catalogue that never covers the API. The usage research found that the dedicated read tools are almost never
called (ex. `inventor_parameters` 6 times against `inventor_eval_csharp` 444 times), and concluded that a better
snippet tool helps more than more tools. The server has 35 tools. Visual Studio Code has a limit of 128 tools for
one chat request, over all servers.

**Counts from the audit logs.** 4,437 C# snippets, of which 222 have a failed result line. A count is the number
of snippets that match a text pattern. The snippets of the server's own tools are in the same log, and the
prelude is part of each snippet that calls a helper, so each count is an upper limit.

| Candidate | Snippets | Note |
| --- | --- | --- |
| Open a document | 2,615 | Mostly the prelude text. The helper `OpenOrReuseDocument` exists |
| Find by name | 1,415 | Mostly loops of the composed tools |
| Save | 436 | 27 in failed snippets |
| Occurrence state | 163 | |
| Representations and model states | 86 | |
| Camera | 72 | Includes `inventor_export_sheet_image` |
| Export to PDF or image | 46 | 7 in failed snippets, the highest failure share |
| Place a component | 35 | |
| Transactions | 20 | |
| iPart and iAssembly tables | 9 | |
| Selection | 8 | |
| Materials and appearances | 5 | |
| Document settings | 4 | |
| iLogic event triggers | 1 | |
| Delete a parameter or a property | 1 | |

**Verdict for each row.**

| Work | Verdict | Reason |
| --- | --- | --- |
| Selection | Tool | Read-only, and it is the only way to know what the user points at. The low count shows that models do not think of it, which a tool with a description corrects |
| Save with the release guard | Tool | Frequent, and the guard is an instruction in `ServerInstructions.md` today. A model that does not read it saves a file that an older release cannot open, and that cannot be undone |
| Export to PDF | Prelude helper | A trap (the translator add-in and its options). A helper costs no tool description |
| Open a document | Nothing | The helper exists |
| Find objects by type and name | Nothing | `inventor_features` and `inventor_assembly_tree` cover most of it |
| Occurrence state | Skill example | Frequent, no trap, three lines |
| Place a component | Skill example | A trap, but with many options (model state, Content Center) that a tool would have to copy |
| Representations | Skill example | `inventor_file_info` reads them |
| iPart tables, event triggers, materials, settings, camera, delete | Skill example, when a session needs one | Rare |

## Skills over MCP

**The state changed.**

- The pull request that the task names (csharp-sdk#1864) was closed with no merge. The competing pull request
	(#1856) was merged, and `ModelContextProtocol.Extensions.Skills` 2.4.0 was released on 2026-11-06.
- The package needs `ModelContextProtocol` 2.4.0 exactly. This repository uses 2.0.0.
- The API is experimental (diagnostic `MCPEXP005`), so it can change with no major version.
- The names differ from the task: `WithSkillsFromSubdirectories` and `WithSkillFromDirectory` (not
	`WithSkillsFromDirectory`), `McpServerSkillPackage.CreateFromDirectory`, and `McpServerSkill.Create`.
- The client matrix still has no Skills support for Claude Code, Claude Desktop, Visual Studio Code or Cursor.
	Only ChatGPT, fast-agent and MCP Inspector have partial support.

**Verdict.** The condition of the task ("start when the package has a release") is true, but the purpose is not:
no client of this server reads the extension. To serve it now adds an experimental dependency and an SDK upgrade
for no user. The SDK upgrade from 2.0.0 to 2.4.0 has its own risk (`inventor_start` uses the input request of
2026-07-28), so it is a separate step that can be done at any time. `Docs/Architecture.md` says that the SDK has
no release with the extension, which is now out of date.

## Live verification

Not a feature. Three items change because of this analysis:

- "Two Inventor processes of the same release" is answered for the pipe behavior by the probe. A live run is
	only a confirmation, and the pipe change removes the case.
- "The server's throttle of `All pipe instances are busy`" and the checks in the connection diagnosis task can be
	one session.
- The plans add live checks (transactions, edit in place, model states). They are listed there, so that one
	Inventor session can do all of them.

`Docs/Tasks/Live-Verification.md` now gives the procedure and the expected result for each item.
