# Autodesk Inventor MCP Server

An MCP server that exposes a live Autodesk Inventor 2027 session.
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

`Inventor.Environment`, `Inventor.File`, `Inventor.Path`, `Inventor.Attribute` and others shadow their
`System` counterparts. Fully qualify the `System` one inside the add-in.

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

## Build and test

```
dotnet build InventorMcp.slnx
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -p:DeployAddIn=true   # development install
dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -c Release -p:DeployBundle=true   # bundle install
```

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
