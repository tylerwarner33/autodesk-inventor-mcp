# Autodesk Inventor MCP Server

An MCP server that exposes a live Autodesk Inventor session, 2025 through 2027.
See `README.md` for setup and `Docs/Tasks/` for the plan and the verification record.

## Architecture in one line

Claude talks stdio to `InventorMcp.Server`, which talks over a named pipe to `InventorMcp.AddIn`,
which runs inside `Inventor.exe` and calls the COM API on Inventor's main thread.

## Rules that come from how Inventor actually behaves

These were each learned by getting them wrong against a live session.
They are not style preferences.

### A named face means the ViewCube face, and the mapping must be queried

When a user says "the top face", they mean the face labelled on the Inventor ViewCube.

**The ViewCube can be redefined per document, so its mapping to world axes is not a constant.**
Call the `inventor_orientation` tool before writing geometry that refers to a named face.

On an unmodified part the mapping measures as Top +Y, Front +Z, Right +X, so **Y is the vertical axis, not Z**.
Treat that as the default to expect, never as the answer.
Assuming a Z-up convention borrowed from other CAD tools puts the work on the Front face.

### Feature extent direction is relative to the sketch, not the world

`PartFeatureExtentDirectionEnum` is relative to the **sketch's own normal**.
A sketch built on a face has its own coordinate system, which may point the opposite way from the face's world normal.

A hole drilled away from the solid removes nothing, and Inventor reports that as `kDriverLostHealth`
rather than a compute error, which looks like a lost reference instead of a wrong direction.

Never assume the direction. Create the feature, `Update`, then count the resulting cylindrical faces or check
`HealthStatus`. If nothing was cut, delete it and retry the other way.

### Lengths are centimetres and angles are radians

Inventor stores every length in centimetres and every angle in radians, whatever the document displays.
A parameter reading `"2 in"` has an internal value of 5.08.

Convert explicitly with `UnitsOfMeasure.ConvertUnits(value, kInchLengthUnits, kDatabaseLengthUnits)`.
Do the conversion once at the top of a script so every literal below reads in the user's units.

### Not every parameter is numeric

A parameter's kind comes from its unit string through `UnitsOfMeasure.GetTypeFromString`:
`kTextUnits` and `kBooleanUnits` mean it has no numeric value at all, and reading `Parameter._Value` on one throws.

A text parameter's `Expression` comes back wrapped in quotation marks.
Writes go through `Value` for text and boolean, and through `Expression` for numeric.

### Use the document's UnitsOfMeasure, not the application's

`Application.UnitsOfMeasure` has no document scope, so it cannot resolve a parameter name.
Validating `"width / 4"` against it always fails. Use `Document.UnitsOfMeasure`.

### Inventor type names collide with the BCL

`Inventor.Environment`, `Inventor.File`, `Inventor.Path` and others shadow their `System` counterparts.
Fully qualify the `System` one inside the add-in.

### Some COM members cannot be C# properties

When the COM getter and setter have different types, the language cannot form a property and the accessor must be
called directly. Known cases:

| Member | Call instead |
| --- | --- |
| `Parameter.Units` | `parameter.get_Units()` |
| `iLogicAutomation.Rules` | `iLogic.get_Rules(document)` |

Expect more. The compiler error names the accessor to use, so read it rather than working around it.

### Other interop facts that are not obvious

- `Parameter._Value` is the database unit double. `Parameter.Value` is typed as `object` and boxes the same number.
- `HealthStatusEnum` has no warning member. The real members include `kCannotComputeHealth`,
	`kInconsistentHealth` and `kRedundantHealth`.
- `ErrorManager` exposes no entry collection. It offers `AllMessages` as one text blob plus `HasErrors` and
	`HasWarnings`, so there is no genuine per entry severity. See `Docs/Tasks/Feature-Error-Messages.md`.
- `Transaction.DisplayName` carries the command name behind each modelling operation, which is what makes the
	activity feed readable.
- `PartFeature` carries no failure text, only `HealthStatus`.
- Assembly feature collections can hold entries that do not expose `PartFeature`; those are skipped.

## Working on this repository

### Changing the add-in costs an Inventor restart

Rebuilding `InventorMcp.AddIn` requires **Inventor closed**.
Unloading the add-in through the Add-In Manager runs `Deactivate` but does not release the assembly,
because Inventor's assembly load context is not collectible, so the build still fails with a file lock.

This is why the add-in stays thin. Prefer the server.

### Prefer composing server-side tools over new bridge operations

Now that `inventor_eval_csharp` exists, a new tool can often be a canned snippet sent through it,
entirely in the server. That needs no add-in rebuild and therefore no Inventor restart.
`inventor_orientation` is built this way. Add a bridge operation only when a snippet genuinely cannot do the job.

### Look the API up rather than guessing

`inventor_api_lookup` searches Autodesk's own documentation and works with Inventor closed.
Use it to confirm a member exists and what it takes before writing a snippet.

### Verify against the model, not the return value

An Inventor API call can succeed while the model is wrong.
Check `inventor_health` after any write, and count geometry when a feature is meant to cut material.

### Assembly isolation is load bearing

The manifest sets `UseInventorAssemblyContext` to `0`, which loads the add-in in its own `AssemblyLoadContext`.
The element names whether to use *Inventor's* context, so `0` is the isolated mode.

This is absorbing a real conflict, not a precaution.
Measured in a live session: the process holds 273 assemblies across 19 load contexts, and
`Microsoft.CodeAnalysis` is loaded twice, 4.13.0.0 in the default context because iLogic is built on Roslyn,
and 4.14.0.0 in this add-in's context.

Two consequences:

- `Autodesk.Inventor.Interop` must stay `Private=false`, so Inventor's types resolve from Inventor while the
	add-in's own packages resolve from its output folder.
- Roslyn scripting needs `InteractiveAssemblyLoader.RegisterDependency` for the globals and interop assemblies.
	Without it Roslyn loads a second copy of the add-in into its own context and the globals object fails to cast.
	`EnterContextualReflection` does not help, because the scripting host does not consult it.
- **`RegisterDependency` is only a fallback.** Roslyn's load context asks the default context first and reaches
	registered dependencies only when that fails. So the add-in must **never** be loaded into the default context,
	or scripts bind to that copy and the globals cast fails. This was measured on Inventor 2025.
- A closed Inventor session must not wedge the server. Disposing the pipe's `StreamWriter` flushes, and that throws
	on a dead pipe, so `BridgeClient.CloseAsync` tolerates `IOException`. Without that, the reconnect never runs.

### Inventor 2025 and 2026 isolate the add-in through a loader shim

Those releases ignore `UseInventorAssemblyContext`. There, the manifest names `InventorMcp.AddIn.Loader`, the only
assembly loaded into the default context. It loads the add-in from an `App\` subfolder into an isolated context,
so exactly one copy of the add-in exists in the process.

Do not replace this with a self isolating add-in that reloads its own DLL. That leaves a copy in the default
context, and the rule above then breaks the execution tools. It was tried and failed.
See `Docs/Tasks/Multi-Version-Support.md`.

To check isolation, list `AssemblyLoadContext.All`. A type asked from inside a snippet reports the snippet's
binding, not the add-in's.

## What is verified, and what is not

Everything below was checked against a live Inventor 2027 session, including a production assembly with
23 open documents, 91 parameters and eight iLogic rules.

Verified: the pipe and main thread dispatcher, the activity feed, parameter reads and writes across numeric, text
and boolean kinds, expression evaluation, iProperty reads and writes, the assembly tree including suppressed
occurrences and the node and depth budget, health reporting including a genuinely sick feature, document targeting
by name, the error paths for a missing document and a wrong document type, `inventor_update`, both execution tools,
the API lookup, and assembly isolation.

Inventor 2025 and 2026 were verified through the loader shim: the session, the API lookup, both execution tools,
and isolation with iLogic's own Roslyn loaded. iLogic ships Roslyn **4.6.0.0 on 2025** and 4.13.0.0 on 2026 and
2027. iLogic loads it lazily, so run a rule before checking the load contexts, or the collision is never tested.

Not yet exercised: the ring buffer's `droppedEntries` counter, which needs more than 2000 buffered events.
Two known gaps have their own task documents, `Busy-Inventor-Call-Rejection.md` and `Feature-Error-Messages.md`.

Note that `inventor_update` marks a clean document dirty even when the rebuild changes nothing,
so it is not a read only call.

## Build and test

```
dotnet build InventorMcp.slnx
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -p:AutodeskVersion=2025 -p:DeployAddIn=true
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -c Release -p:AutodeskVersion=2025 -p:DeployBundle=true
```

`AutodeskVersion` selects the Inventor release and defaults to 2027. Supported: 2025, 2026, 2027.
The add-in builds to `bin\<configuration>\<version>`, so the three releases never overwrite each other.
Run the bundle command once per release: each adds its own manifest and `Contents` subfolder to one
version independent bundle, and Inventor loads only the manifest matching its own version.
See `Docs/Tasks/Multi-Version-Support.md`.

Logs:

| Path | Contents |
| --- | --- |
| `%LOCALAPPDATA%\InventorMcp\addin.log` | Add-in lifecycle and handler failures |
| `%LOCALAPPDATA%\InventorMcp\server-<date>.log` | MCP server activity |
| `%LOCALAPPDATA%\InventorMcp\executed-code.log` | Every snippet run through the execution tools |

### Driving the server by hand

The server is stdio, so a test client must **hold standard input open**.
Closing it immediately makes the transport reach end of input before the host starts: handlers run, but responses
go nowhere. The log gives it away, with "transport completed reading messages" appearing before "Application started".

A server process whose stdin never closes stays alive and locks `InventorMcp.Server.exe`, which then blocks a rebuild.
