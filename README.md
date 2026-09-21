# Autodesk Inventor MCP Server

An MCP server that lets Claude inspect and drive a live Autodesk Inventor 2027 session.
It is built for developing Inventor plugins and automation, so Claude can see what a running automation loop creates, in order, as it runs.

## Architecture

```
Claude Code
    | stdio
InventorMcp.Server            net10.0    every MCP tool lives here
    | named pipe  InventorMcp.Bridge     restricted to the current Windows user
InventorMcp.AddIn             net10.0    thin bridge: pipe listener, dispatch, main-thread marshaling
    | in-process COM on Inventor's main thread
Inventor.exe 2027
```

The add-in stays thin on purpose.
Changing it costs an Inventor restart, while the tool surface changes constantly during development.

## Documentation

| Path | Audience |
| --- | --- |
| `Docs/Setup-and-Usage-Guide.md` | Users: install, connect Claude, what to ask for, troubleshooting |
| `Docs/Tasks/` | The plan, the reference repository findings, and the verification record |
| `.claude/CLAUDE.md` | Claude: the Inventor behaviours and repository rules to work by |

## Projects

| Project | Purpose |
| --- | --- |
| `Source/InventorMcp.Contracts` | The pipe wire contract. Holds no Inventor interop, so the server builds without Inventor installed. |
| `Source/InventorMcp.AddIn` | The Inventor add-in. Owns the pipe listener and main thread dispatch only. |
| `Source/InventorMcp.Server` | The MCP server. Owns every tool. |
| `Libs/Inventor/2027` | The vendored Inventor interop assembly, so a build agent without Inventor can still build. |

## Requirements

- .NET 10 SDK
- Autodesk Inventor 2027 to run. Not needed to build, because the interop assembly is vendored under `Libs`.

The Inventor release is set once, by `AutodeskVersion` in `Directory.Build.props`.

## Setup

Build everything and deploy the add-in manifest:

```
dotnet build InventorMcp.slnx
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -p:DeployAddIn=true
```

The second command writes `InventorMcp.AddIn.addin` to `%APPDATA%\Autodesk\Inventor 2027\Addins`.
The manifest points at the build output, so later rebuilds need no redeployment.

Restart Inventor.
Confirm the bridge started by checking `%LOCALAPPDATA%\InventorMcp\addin.log`.

## Connecting Claude

The server is a stdio MCP server, so Claude starts it. There is nothing to leave running and no port.

It is started as the built executable rather than through `dotnet run`, because MSBuild writes to standard output and would corrupt the protocol stream.

### Claude Code

`.mcp.json` in the repository root is picked up when Claude Code runs in this directory.
Nothing else is needed. If the relative command path does not resolve, replace it with the full path to
`Source\InventorMcp.Server\bin\Debug\InventorMcp.Server.exe`.

### Claude Desktop

Claude Desktop does not read `.mcp.json`. Add the server to `claude_desktop_config.json` with an absolute path:

```json
{
	"mcpServers": {
		"autodesk-inventor": {
			"command": "C:\\repos\\_MyProjects\\autodesk-inventor-mcp\\Source\\InventorMcp.Server\\bin\\Debug\\InventorMcp.Server.exe",
			"args": []
		}
	}
}
```

## Inventor must already be running

The add-in hosts the named pipe, so the bridge exists only while Inventor is open with the add-in loaded.

Claude cannot start Inventor. Every tool returns a readable `inventor-not-running` result instead:

```
No Inventor session is hosting the MCP bridge.
Start Inventor 2027 and make sure the Inventor MCP Bridge add-in is loaded.
```

The MCP server itself still starts and lists its tools, so the connection stays healthy while Inventor is closed.
That is the reason for the two process design.

### Debugging the add-in

The add-in project sets `StartProgram` to Inventor, so pressing F5 on it launches Inventor with the debugger attached.
Deploy the manifest once first, otherwise Inventor has nothing to load.

**Inventor must be closed to rebuild the add-in.**
Unloading it through the Add-In Manager runs `Deactivate` but does not release the assembly,
because Inventor's assembly load context is not collectible, so the build still fails with a file lock.

This is the reason the add-in stays thin.
Every line in it costs a CAD restart to change, while the server's tools can be changed freely.

### Bundle deployment

For a release style install, which does not point back into a build folder:

```
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -c Release -p:DeployBundle=true
```

This writes a self contained `InventorMcp.AddIn.bundle` with its `PackageContents.xml` to the same add-ins folder.
Use one form or the other, not both, or Inventor loads the add-in twice.

### Assembly isolation

The manifest sets `UseInventorAssemblyContext` to `0`, which loads the add-in in its own `AssemblyLoadContext`.
The element names whether to use *Inventor's* context, so `0` is the isolated mode.
This keeps the add-in's dependencies from clashing with the versions Inventor and other vendors' add-ins load into the same process.

The `Autodesk.Inventor.Interop` reference must stay `Private=false` for this to work.
The interop has to resolve from Inventor itself, while the add-in's own packages resolve from its output folder.

## Tools

| Tool | Purpose |
| --- | --- |
| `inventor_session` | Is Inventor reachable, which version, which document is active |
| `inventor_documents` | Every open document with path, type, and unsaved state |
| `inventor_assembly_tree` | Occurrence tree with suppression, visibility, and referenced files |
| `inventor_parameters` | Parameters with kind, expression, display value, and internal value |
| `inventor_set_parameter` | Set one parameter and rebuild |
| `inventor_evaluate_expression` | Ask Inventor what an expression evaluates to, without writing it |
| `inventor_properties` | iProperties, optionally limited to one set |
| `inventor_set_property` | Set one iProperty |
| `inventor_health` | Rebuild state, sick features, error manager contents |
| `inventor_update` | Rebuild a document |
| `inventor_activity` | Ordered feed of Inventor events, including every committed transaction |
| `inventor_eval_csharp` | Run a C# snippet against the live Inventor API |
| `inventor_run_ilogic` | Run an iLogic rule body, in VB.NET, against a document |
| `inventor_api_lookup` | Search Autodesk's Inventor API documentation. Works with Inventor closed. |
| `inventor_orientation` | Resolve each ViewCube face to a world direction for the active document |

## Executing code

`inventor_eval_csharp` is the general purpose tool. It reaches every member of the Inventor API, so it covers
everything the narrower tools do not, such as creating sketches, features, and geometry.

The snippet is Roslyn script code rather than a full class.
`Application` and `Document` are in scope, `System`, `System.Collections.Generic`, `System.Linq` and `Inventor`
are imported, and `Log(...)` records a line for the caller.

`inventor_api_lookup` answers what a member is called and what it takes, before a snippet is written.
It reads the vendored documentation file, so it needs no Inventor session.

### Safeguards

Arbitrary execution inside a CAD process is the most dangerous capability here, so it is bounded:

- Every snippet is appended to `%LOCALAPPDATA%\InventorMcp\executed-code.log`, with a timestamp and the target document.
- Execution is refused when the document has unsaved changes, unless `allowUnsavedChanges` is passed.
	Unsaved work cannot be recovered if a snippet damages it.
- Roslyn loads on first use only, so a read-only session never pays for it inside Inventor's process.
- Execution runs inside a `SilentOperationScope`, so a dialog cannot stall the dispatcher.

A snippet that throws is reported as a result, with its exception type and message, rather than failing the call.
Compiler errors come back as diagnostics.

## Units and parameter kinds

Inventor stores every length in centimetres and every angle in radians, whatever a document displays.
Parameter results therefore carry `internalValue` in those units alongside `expression` and `displayValue`.

Not every parameter is numeric.
Each one reports a `valueKind` of `Numeric`, `Text`, `Boolean`, or `Unknown`, because a write uses a different accessor for each.
A text parameter has no internal value, and its expression is returned with the surrounding quotation marks removed.

## Writes and dialogs

A modal dialog owns Inventor's message pump, and the bridge posts its work to that pump.
So an unanswered dialog stalls every tool except `inventor_activity`, which reads plain managed memory.

Write operations therefore run with `SilentOperation` set, and restore the previous value afterwards.
Inventor answers a suppressed dialog with its own default, which is a real behaviour change, so reads never suppress anything.

## Logs

| Path | Contents |
| --- | --- |
| `%LOCALAPPDATA%\InventorMcp\addin.log` | Add-in lifecycle and handler failures |
| `%LOCALAPPDATA%\InventorMcp\server-<date>.log` | MCP server activity |
