# Autodesk Inventor MCP Server - Implementation Plan

Created: 2026-09-21

Status: **build order steps 1 to 6 implemented and verified against a live Inventor 2027 session on 2026-09-21.**
The bridge, the main thread dispatcher, the activity feed, the parameter reads, and the write path all work.
One bug was found by running it, fixed, and re-verified against a live session.
Steps 7 and 8 are implemented and verified: real geometry, four holes and four corner fillets, was modelled through
`inventor_eval_csharp` and confirmed visually in the Inventor window.
All eight build order steps are now complete.
Steps 7 and 8 are not started.
Every finding in the sibling `Reference-Repo-Findings.md` was applied on 2026-09-21; see the Verification section below for what that did and did not prove.

## Goal

Let Claude inspect and drive a live Autodesk Inventor 2027 session while the user develops Inventor plugins and automation.
Claude must see the elements the running automation creates, in order, as the automation loops.

## Decisions

| Decision | Choice |
| --- | --- |
| Process layout | Two processes joined by a named pipe |
| Inventor versions | 2027 only |
| Access level | Read, scoped writes, and in-session code execution |
| Code execution | iLogic rules (VB.NET) and C# expression evaluation through Roslyn |
| Transport to Claude | stdio |
| API reference source | `Autodesk.Inventor.Interop.xml`, not dll2llm |

### Why two processes

An add-in change costs an Inventor restart.
The tool surface changes constantly during development; the bridge stabilizes quickly.
Putting the tools in a separate process keeps the edit and test cycle free of CAD restarts.
The server also stays alive when Inventor closes, so Claude reads a clear "Inventor is not running" result instead of a dead MCP server.

### Why a named pipe

A named pipe is kernel backed and needs no TCP port.
`PipeSecurity` restricts the pipe to the current user account.
A loopback TCP port is reachable by every process and every logged on user unless a shared secret is added.
That access control difference matters because the endpoint executes arbitrary code inside the CAD process.

### Why stdio

The server is a single user developer tool, and the named pipe already carries the trust boundary.
stdio means Claude Code starts the server itself.
There is no port, no token, and no URL to keep in sync.

### Why not dll2llm

`Autodesk.Inventor.Interop.xml` ships next to the interop assembly and already holds Autodesk's member documentation.
dll2llm would reflect over the assembly to re-derive close to the same content.
The real problem is that the file is 11.9 MB and does not fit in context, so the work is condensing and indexing it.

## Architecture

```
Claude Code
    | stdio
InventorMcp.Server            net10.0    every MCP tool lives here
    | named pipe  InventorMcp.Bridge     PipeSecurity: current user only
InventorMcp.AddIn             net10.0    thin and stable: pipe listener, dispatch, marshaling
    | in-process COM on Inventor's main STA thread
Inventor.exe 2027
```

The pipe name is fixed rather than carrying the process id.
A fixed name lets the server connect with no discovery step.
A second Inventor instance cannot claim the pipe and logs the conflict instead of competing for it.

`InventorMcp.Contracts` is a shared `net10.0` class library holding the request and response records.
The server references it for typing.
The add-in references it for dispatch.
Keeping the wire contract in one project is what stops the two halves drifting.

### Repository layout

```
Docs/Tasks/                       plan and task documents
Source/InventorMcp.Contracts/     shared wire contract
Source/InventorMcp.Server/        MCP server, stdio host, all tools
Source/InventorMcp.AddIn/         Inventor add-in, pipe bridge only
```

## Technical constraints

### Inventor 2027 add-ins load on .NET 10

`Bin/ClrAddinLoader.runtimeconfig.json` targets `net10.0` against `Microsoft.NETCore.App 10.0.0`.
Inventor 2024 used .NET Framework 4.8.
The move to modern .NET means all three projects share one target framework.

### COM objects belong to Inventor's main STA thread

A pipe request arrives on a worker thread.
The add-in must marshal each call onto Inventor's main thread.
Use a hidden window `SynchronizationContext` created during `Activate`, or an idle pump driven by `ApplicationEvents`.
Calling COM straight from the pipe listener thread produces intermittent `RPC_E_WRONG_THREAD` faults.

### Internal units are centimetres and radians

`Parameter.Value` returns centimetres whatever the document displays.
`Parameter.Expression` returns the display string.
Every tool that reports a value returns both the internal value and the expression.

### The activity feed is a pull, not a push

The add-in subscribes to `ApplicationEvents`, `TransactionEvents`, and the document level event sets.
Each event appends to a bounded ring buffer of 2000 entries, each timestamped and sequence numbered.
Claude calls `inventor_activity(since_sequence)` to drain it.
Pushing MCP notifications instead would flood the model during a tight automation loop, which is the moment the feed must keep working.

## Tool surface

### Session and documents

- `inventor_session` - is Inventor running, version, active document, open document count
- `inventor_documents` - open documents with full path, type, and dirty state
- `inventor_assembly_tree` - occurrence tree with suppression, visibility, and referenced file per node

### Parameters and properties

- `inventor_parameters` - model, user, and reference parameters with kind, expression, internal value, units, and driven state
- `inventor_set_parameter` - set a parameter, using the accessor its kind requires (scoped write)
- `inventor_evaluate_expression` - evaluate an expression without writing it, reporting validity and driving parameters
- `inventor_properties` - iProperties for the active or a named document
- `inventor_set_property` - set an iProperty (scoped write)

### Health and rebuild

- `inventor_health` - feature health, sick features, update state, and last failure detail
- `inventor_update` - rebuild the active document (scoped write)

### Activity

- `inventor_activity` - drain the event ring buffer from a sequence number

### Code execution

- `inventor_run_ilogic` - run an iLogic rule body against a document, added and removed temporarily
- `inventor_eval_csharp` - evaluate a C# snippet through Roslyn with the interop referenced and `Application` in scope

This pair is what makes the server complete against the Inventor API.
A tool for each modelling operation would be an endless catalogue that never covers the API,
whereas one execution tool reaches every member, and `inventor_api_lookup` supplies the names.

### API reference

- `inventor_api_lookup` - query the vendored `Autodesk.Inventor.Interop.xml` for a type or member.
	Server side, so it works with Inventor closed.

## Safeguards on code execution

Arbitrary code execution inside the CAD process against unsaved work is the most dangerous part of this server.
It is included deliberately, because it is what makes the server useful for plugin development.

- Every executed snippet is written to a rolling log file with a timestamp
- Execution is refused when the active document has unsaved changes, unless an explicit override argument is passed
- Roslyn scripting loads lazily on first use, so a read-only session never pays for it inside Inventor's process

## Build order

1. Done - root scaffolding: `Directory.Build.props`, central package management, `InventorMcp.slnx`, `.mcp.json`
2. Done - `InventorMcp.Contracts`: request and response records, the pipe envelope
3. Done - `InventorMcp.AddIn`: `ApplicationAddInServer`, pipe listener, main thread dispatcher, handler table, `.addin` manifest deployment
4. Done - `InventorMcp.Server`: stdio host, pipe client, session and document tools
5. Done - parameters, properties, and health tools
6. Done - activity ring buffer and feed
7. Done - iLogic and C# execution with safeguards
8. Done - `inventor_api_lookup`, reading the vendored documentation

Step 8 landed in the server rather than the add-in.
The documentation is a plain file, so a lookup needs no Inventor session and no add-in rebuild to improve.
That also means API questions can be answered with Inventor closed.

All eight steps are complete and exercised against a live Inventor 2027 session. See Verification below.

A ninth capability, `inventor_orientation`, was added afterwards and is not a build order step.
It resolves each ViewCube face to a world direction by querying the camera.
It is composed from `inventor_eval_csharp` entirely in the server, so it needed no add-in rebuild.
That is now the preferred way to add a capability: a canned snippet costs no Inventor restart, a bridge operation does.

## Verification

### 2026-09-21 - the bridge works end to end against a live Inventor 2027 session

Driven against Inventor 2027 build 310192000, process 41180, with a new part holding four model parameters.
A second Inventor 2025 process was running for unrelated work; it cannot load this add-in, because the manifest
requires a version above 30, so it never competed for the pipe.

Proved, all against the running session:

- **`MainThreadDispatcher`.** The message only window, the `WndProc` drain, and the GC rooted delegate marshal
	real COM calls. This was the highest risk component in the design.
- **The named pipe.** Request and response over newline delimited JSON, in both directions, across many calls.
- **`ActivityRecorder`.** Both event sets fire and land in the ring buffer.
	`Transaction.DisplayName` produced exactly the narration the design hoped for:
	`New Document`, `Create Box`, `Edit Dimension Value`, `Update Part Document`.
	Document events fired alongside: `OnNewDocument`, `OnActivateDocument`.
	The sequence cursor and `droppedEntries` behaved correctly across separate drains.
- **Parameter reads, and the units design.** A parameter reading `"2 in"` reported `internalValue` 5.08.
	That is 2 inches in centimetres, so any tool reporting a single form would have been wrong by a factor of 2.54.
- **The write path, including `SilentOperationScope`.** Setting `width` to `6 in` changed the model, raised no
	dialog, and did not stall the dispatcher.
- **`inventor_evaluate_expression`.** Resolved `width * 2 + 0.5 in` to 4.500 in and named `width` as the driving parameter.
- **`inventor_health`.** Reported a clean document after the write: no sick features, no errors.
- **`Deactivate`.** Unloading the add-in logged a clean stop with no failure from the COM release path.

### 2026-09-21 - bug found by running it: the wrong UnitsOfMeasure

`inventor_set_parameter` rejected `width / 4` with "Inventor cannot parse ... as a value in 'in'",
while `inventor_evaluate_expression` evaluated the same expression to 1.500 in.

Cause: `SetParameter` validated against `Application.UnitsOfMeasure`, which has no document scope and therefore
cannot resolve a parameter name. `EvaluateExpression` used `Document.UnitsOfMeasure`, which can.
So the validation guard rejected every expression referencing another parameter, which is the most valuable kind
of write and the one a configurator depends on.

The guard itself came from finding 5 and was meant as a safety improvement.
Without it the assignment would simply have succeeded, so the improvement introduced the failure.
It is still worth keeping, scoped to the document.

Fixed by passing the document's `UnitsOfMeasure` into `Describe`, `DetermineValueKind`, and the validation call.

### 2026-09-21 - steps 7 and 8 verified by modelling real geometry

Four holes and four corner fillets were added to `Part2.ipt` entirely through `inventor_eval_csharp`,
and confirmed visually by the user against the Inventor window.

Proved:

- **Roslyn runs inside Inventor under the isolated assembly load context.** Scripts see the interop types and the
	globals object, and a first run costs about 900 ms.
- **Compiler errors return as clean diagnostics** with line and column, ex.
	`(72,37): error CS0266: Cannot implicitly convert type 'SketchHolePlacementDefinition' to 'HolePlacementDefinition'`,
	and **nothing reaches the model** when compilation fails.
- **A snippet that throws returns a structured result**, with exception type and message, rather than failing the tool.
- **The audit log records every snippet.**
- **`inventor_api_lookup` supplied every signature used**, including the `holeCenter: true` argument on
	`SketchPoints.Add` that a hole feature requires. All of it was researched with Inventor closed.
- **`inventor_health` caught a failure the execution result reported as success**, which is the clearest argument for
	keeping the two separate. See below.

#### Bug found: the hole cut nothing, and the API said it succeeded

Every API call returned successfully and the script reported "Created 4 holes", but `inventor_health` showed
`Hole1` as `kDriverLostHealth`. Counting faces settled it: 10 faces meant 6 box faces plus 4 fillet cylinders,
so the holes had cut no material at all.

A suppression test disproved the first hypothesis, that the fillet had invalidated the sketch: the hole stayed
`DriverLost` with the fillet suppressed. The real cause, identified by the user from the Inventor window, was that
the hole was drilling away from the solid. See the extent direction note in the orientation section above.

The rebuild scripts now verify the cut by counting cylindrical faces and retry in the opposite direction if nothing
was removed, rather than assuming.

#### Bug found: the work landed on the Front face

The first correct build put the holes on the +Z face, which Inventor labels Front.
A named face means the ViewCube face, and Top is +Y. The features were deleted and rebuilt on +Y.

The same wrong assumption had also produced a false ambiguity about "half the height of the box".
With Y up, the parameter named `height` is the vertical dimension, so the user's original wording was exact
and the correct hole depth was 0.75 in.

### 2026-09-21 - the two fixes verified after an Inventor restart

Verified against a second part, `Part2.ipt`, carrying numeric, unitless, text, and multi value text user parameters.

- **Text parameters work, which finding 4 existed for.**
	`TestText` returned `valueKind` of `Text`, its expression with the surrounding quotation marks removed,
	and no `internalValue` at all. The unit string comes back as `Text`, so `GetTypeFromString` is a reliable
	discriminator. Writing `Written by Claude` through the `Value` accessor succeeded and read back clean.
- **The whole parameter list survived a text parameter.**
	Under the original code `parameter._Value` would have thrown on `TestText`, and with no per parameter guard
	that single parameter would have failed the entire call. All eight came back.
- **The `UnitsOfMeasure` fix works.**
	`height = width / 4` now succeeds, where it was rejected before the fix.
	Inventor normalised the stored expression to `width / 4 ul` and reported `internalValue` 3.81, which is 1.500 in.
- **`isDriven` reports true for the first time**, on the newly driven `height`, so that path is verified too.
- **Unitless parameters** read correctly: `12 ul` gives `internalValue` 12 with units `ul`.
- **The activity feed narrated the whole session**, 20 events across open, save, and every dimension edit,
	including both writes made through the bridge, with no dropped entries.

Note that a text parameter write also commits as `Edit Dimension Value` followed by `Update Part Document`,
so the feed does not distinguish a text edit from a numeric one by transaction name alone.

### 2026-09-21 - an add-in change always costs an Inventor restart

Unloading the add-in through the Add-In Manager runs `Deactivate` and logs a clean stop,
but it does **not** release `InventorMcp.AddIn.dll`. A rebuild still fails with:

```
error MSB3027: The file is locked by: "Autodesk Inventor 2027 (41180)"
```

Inventor's assembly load context is not collectible, so unloading the add-in does not unload its assemblies.
There is no way to iterate on add-in code without closing Inventor.

This strengthens the original reason for the two process split rather than weakening it:
the add-in must stay thin, because every line in it costs a CAD restart to change,
while the server's tools can be changed freely.

### 2026-09-21 - the MCP server runs, driven by a real stdio client

The server was driven over stdio by a PowerShell harness that holds standard input open, as a real client does.

Proved:

- `initialize` completes and the server reports its capabilities.
- `tools/list` publishes all 11 tools with their schemas.
- `tools/call` on `inventor_session` returns the structured `inventor-not-running` result.
	This is the important one: with no Inventor running, the server degrades to a readable message instead of hanging or failing the connection, which means the pipe connect timeout path works.

Two bugs found by this run, both fixed:

- **Every optional tool argument was published as required.**
	`inventor_assembly_tree` advertised `"required":["documentName","maxDepth","maxNodes"]`.
	The SDK marks a parameter required unless it carries a C# default value, and none did.
	A caller would have been forced to pass nulls and invent numbers.
	Fixed by giving every optional argument a default. The published schemas now mark only
	`expression`, `name` and `expression`, and `setName`, `name` and `value` as required.
- **A probe that closes standard input immediately proves nothing.**
	The transport reaches end of input before the host starts, so handlers run but responses go nowhere.
	The log ordering gives it away: "transport completed reading messages" appears before "Application started".
	Any future harness must hold standard input open.

Still unproved: everything behind the pipe. See below.

### 2026-09-21 - build and deployment only

What was proved, all by build and by reflection against the Inventor 2027 interop assembly:

- The full solution builds clean with `TreatWarningsAsErrors`.
- `Autodesk.Inventor.Interop` resolves from `Libs/Inventor/2027`, confirmed by `dotnet msbuild -getItem:Reference`.
	The build no longer needs Inventor installed, which is what makes `Pipelines/Build.yml` possible.
- `-p:DeployAddIn=true` writes a manifest naming the build output. Verified against a scratch folder.
- `-p:DeployBundle=true` writes the bundle layout with `PackageContents.xml`, a bare assembly name in the manifest, and the satellite resource folders preserved. Verified against a scratch folder.
- Output paths are flat, ex. `bin/Debug`, so `.mcp.json` no longer names a target framework.
- Every Inventor API member used was checked by reflection before being written, not guessed.

What remained unproved at that point was everything behind the pipe.
All of it was proved later the same day; see the session verification above.

Still unproved after all of 2026-09-21:

- **Boolean parameters.** Text and numeric are verified; no boolean parameter has been read or written,
	so the `bool.TryParse` branch has never run.
- **The assembly tree walk.** No assembly has been opened, so `inventor_assembly_tree`, the depth and node
	budget, and the suppressed occurrence guard are all untested.
- **iProperty reads and writes.** `inventor_properties` and `inventor_set_property` have never been called.
- **`inventor_update` as a tool.** Rebuilds happened inside `set_parameter`, never through the update tool itself.
- **Sick feature reporting is verified**, by accident rather than design: the failed hole surfaced as
	`Hole1` with `kDriverLostHealth`, and that report is what caught a bug the API had reported as success.
	Not yet seen: a feature carrying an error manager message, since `errorCount` has been zero throughout.
- **The dispatcher under load**, ex. a real automation loop, and the ring buffer's drop counting on overflow.
- **The `RPC_E_CALL_REJECTED` path.** Inventor was never busy enough to reject a call.
- **Assembly isolation.** The add-in loads with `UseInventorAssemblyContext` set, and nothing has misbehaved,
	but nothing yet exercises a conflicting dependency, which is what isolation exists to prevent.

### Bug found while implementing, 2026-09-21

The deployment targets substitute a token in the manifest template with a plain global string replace.
The token also appeared in the template's own comment, so the comment was rewritten with the assembly path.
Harmless, because it is a comment, but the template now states that the token must appear exactly once.

## Inventor orientation, which any geometry script depends on

When a user names a face, they mean the face labelled on the Inventor ViewCube.

**The ViewCube can be redefined, so its mapping to world axes is a property of the document, not a constant.**
It must be queried rather than assumed:

```csharp
Camera camera = Application.ActiveView.Camera;
camera.ViewOrientationType = ViewOrientationTypeEnum.kTopViewOrientation;
camera.Apply();

// Eye minus Target is the outward world direction of the face being viewed.
Vector direction = Application.TransientGeometry.CreateVector(
	camera.Eye.X - camera.Target.X,
	camera.Eye.Y - camera.Target.Y,
	camera.Eye.Z - camera.Target.Z);
direction.Normalize();
```

Save `Eye`, `Target` and `UpVector` beforehand and restore them afterwards, so the user's view is left untouched.

On an unmodified part the mapping measures as:

| ViewCube face | Outward direction | View up |
| --- | --- | --- |
| Top | +Y | -Z |
| Bottom | -Y | +Z |
| Front | +Z | +Y |
| Back | -Z | +Y |
| Right | +X | +Y |
| Left | -X | +Y |

So on a default document Y is the vertical axis, not Z, and "the height of the box" is its Y extent.
Treat that table as the default to expect, never as the answer.
Assuming a Z-up convention borrowed from other tools puts the work on the Front face.

## What the interop taught us during step 3

These are facts the documentation does not make obvious, recorded so they are not rediscovered.

- `Inventor.Environment` is a real API type, the ribbon environment.
	It collides with `System.Environment`, which must therefore be written out in full.
- `Parameter.Units` cannot be a C# property.
	The COM getter returns a string while the setter takes an object, so the accessor `get_Units()` must be called directly.
- `Parameter._Value` is the database unit double. `Parameter.Value` is typed as `object` and boxes the same number.
- `HealthStatusEnum` has no warning member.
	The real members include `kCannotComputeHealth`, `kInconsistentHealth`, and `kRedundantHealth`.
- `ErrorManager` exposes no entry collection.
	It offers `AllMessages` as one text blob plus `HasErrors` and `HasWarnings`, so each line becomes an entry and there is no per entry severity.
- `Transaction.DisplayName` carries the command name behind each modelling operation.
	This is what turns the activity feed into a readable narration of an automation loop.

## Related documents

`Reference-Repo-Findings.md` surveys six existing Autodesk automation repositories.
It raises four issues that affect correctness here: the interop reference blocks any build agent, the manifest needs `UseInventorAssemblyContext` before Roslyn is loaded, write operations need `SilentOperation` so a modal dialog cannot stall the dispatcher, and the parameter handlers assume every parameter is expression driven.

## Open items

- Claude cannot start Inventor. The add-in hosts the pipe, so it exists only while Inventor is open.
	Launching Inventor over COM is feasible; `Cincinnati/StrobicConfigurator` shows the pattern, using
	`Type.GetTypeFromProgID` and `Activator.CreateInstance`, and the add-in would load into that instance
	because the manifest sets `LoadOnStartUp`. Not implemented, and it should be an explicit opt in
	rather than something a read tool does by surprise.
- `PartFeature` exposes no failure text, so `FeatureHealth.Message` is always empty.
	The error manager blob is the only source of failure detail at present.
- Assembly feature collections can hold entries that do not expose `PartFeature`. Those are skipped.
- Whether the condensed API skill ships in this repository or as a separate Claude skill.
