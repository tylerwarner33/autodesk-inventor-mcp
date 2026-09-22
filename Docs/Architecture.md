# Architecture

Why this tool is built the way it is.
For installing and using it, see `Setup-and-Usage-Guide.md`.

## Shape

```
Claude
    | stdio
InventorMcp.Server            net10.0    every MCP tool lives here
    | named pipe  InventorMcp.Bridge     restricted to the current Windows user
InventorMcp.AddIn             net10.0    thin bridge: pipe listener, dispatch, main thread marshaling
    | in-process COM on Inventor's main thread
Inventor.exe 2027
```

| Project | Purpose |
| --- | --- |
| `InventorMcp.Contracts` | The wire contract. Holds no Inventor interop, so the server builds without Inventor installed. |
| `InventorMcp.AddIn` | Runs inside Inventor. Owns the pipe listener and main thread dispatch only. |
| `InventorMcp.Server` | Owns every tool. |
| `Libs/Inventor/2027` | The vendored interop assembly, so a build agent without Inventor can still build. |

## Decisions

### Two processes rather than one

Inventor's API is in-process COM, not a service, so something has to run inside `Inventor.exe`.
The question was whether the MCP server should run there too.

It does not, because **changing the add-in requires closing Inventor**.
The tool surface changes constantly; the bridge stabilised within a day.
Keeping the tools in a separate process means most work needs no CAD restart.

It also means the MCP connection survives Inventor closing and reopening.
Claude gets a readable "Inventor is not running" result instead of a dead server.

### A named pipe rather than a local HTTP port

The pipe is kernel backed, needs no port, and `PipeSecurity` restricts it to the account that started Inventor.
A loopback TCP port is reachable by every process and every logged on user unless a shared secret is added.

That matters here because the endpoint can execute arbitrary code inside the CAD process.

The pipe name is fixed, so the server connects with no discovery step.
Only one Inventor session can host it; a second logs the conflict rather than competing.

### stdio rather than HTTP to Claude

A single user developer tool, with the pipe already carrying the trust boundary.
stdio means Claude starts the server itself: no port, no token, no URL to keep in sync.

### The add-in stays thin

The add-in holds the pipe listener, main thread marshaling, an event recorder, and a table of primitive handlers.
Everything else is in the server. This is a direct consequence of the restart cost.

Since the C# execution tool exists, a new capability is usually a canned snippet composed in the server rather than
a new bridge operation, so it costs no restart at all.

### Execution rather than a tool per operation

A tool for each modelling operation would be an endless catalogue that never covers the Inventor API.
One C# execution tool reaches every member, and the API lookup tool supplies the names.

That is why the tool surface is small and ends with `inventor_eval_csharp` rather than growing indefinitely.

### The interop assembly is vendored

`Autodesk.Inventor.Interop.dll` and its documentation live in `Libs/Inventor/2027` rather than being referenced
from Program Files, so the repository builds on a machine or build agent with no Inventor installed.
The same documentation file backs the `inventor_api_lookup` tool.

## Behaviours worth knowing

### The activity feed is a pull, not a push

The add-in subscribes to Inventor's own events and appends to a bounded ring buffer of 2000 entries.
Claude drains it on request from a sequence cursor.

Pushing notifications would flood the model during a tight automation loop, which is exactly when the feed matters.
Because the buffer is plain memory rather than a call into Inventor, draining it keeps working while Inventor is busy.

`Transaction.DisplayName` carries the command name behind each operation, which is what turns the feed into a
readable narration: `Create Sketch on a Face`, `Create Hole Feature`, `Edit Dimension Value`.

### Writes suppress Inventor's dialogs

A modal dialog owns Inventor's message pump, and the bridge posts its work to that pump, so an unanswered dialog
would stall every tool except the activity feed.

Write operations therefore set `SilentOperation` and restore it afterwards.
Inventor answers a suppressed dialog with its default, which is a real behaviour change, so reads never suppress.

### The add-in runs in its own assembly load context

The manifest sets `UseInventorAssemblyContext` to `0`, which isolates the add-in's dependencies.

This is not precautionary. Inventor loads Roslyn 4.13 because iLogic is built on it, while this add-in's scripting
brings 4.14, and both are loaded at once in separate contexts. Isolation is what keeps them from having to agree.

### Units

Inventor stores lengths in centimetres and angles in radians whatever a document displays.
Results report both the internal value and the display form, so a caller never has to work out which it received.

## Safety

Code execution inside a CAD process is the most dangerous capability here, and it is bounded:

- Every snippet is appended to an audit log with a timestamp and the target document.
- Execution is refused on a document with unsaved changes unless explicitly overridden.
- Roslyn loads on first use only, so a read-only session never pays for it inside Inventor's process.

A snippet that throws is reported as a result rather than failing the call, and compiler errors come back as
diagnostics with line and column, before anything reaches the model.
