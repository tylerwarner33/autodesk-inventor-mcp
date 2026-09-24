# Autodesk Inventor MCP Server

An MCP server that exposes a live Autodesk Inventor session, 2025 through 2027.
See `README.md` for setup, `Docs/Architecture.md` for the decisions, and `Docs/Tasks/` for outstanding work.

These are the instructions for a coding agent that works on this repository (ex. Claude Code, GitHub Copilot).
The rules a model needs to *use* the Inventor tools travel with the server instead, in
`Source/InventorMcp.Server/ServerInstructions.md`.

## Architecture

An MCP client talks stdio to `InventorMcp.Server`, which talks over a named pipe to `InventorMcp.AddIn`,
which runs inside `Inventor.exe` and calls the COM API on Inventor's main thread.
`InventorMcp.Contracts` holds the bridge messages both sides share.
`InventorMcp.AddIn.Loader` isolates the add-in on Inventor 2025 and 2026.

## Working Principles

- **Keep the add-in thin. Prefer the server.** A rebuild of the add-in needs Inventor closed.
- **Compose server-side tools before adding bridge operations.** A new tool can often be a canned snippet sent
	through `inventor_eval_csharp`, with no add-in rebuild. `inventor_orientation` is built this way.
	Add a bridge operation only when a snippet genuinely cannot do the job.
- **Look the API up rather than guessing.** `inventor_api_lookup` searches Autodesk's own documentation and works
	with Inventor closed. Use it to confirm a member exists and what it takes before writing a snippet.
- **Verify against the model, not the return value.** Check `inventor_health` after any write.
- **Keep evidence out of code comments.** A remark says what a change to the code needs, plus a pointer.
	Dates, measurements and what was tried go in `Docs/` or `.agents/rules/`, never in `Docs/Tasks/`, which is deleted.
	Evidence that a task comes from goes in `Docs/Research/`, and the task points to it.

## On-Demand Rules

The rules are not loaded automatically. Before acting on a task, scan this list and open any rule whose trigger fits
the request or the files about to change. If several match, open all of them.

- **`.agents/rules/inventor-modeling.md`** - Open before writing geometry, sketches, or features in a live session, or when a request names a face (ex. "the top face"), a direction, or units. Covers the ViewCube mapping, sketch-relative extent direction, and centimetres and radians.
- **`.agents/rules/inventor-interop.md`** - Open when editing `Source/InventorMcp.AddIn/`, or writing a snippet that touches parameters, `UnitsOfMeasure`, health, `ErrorManager`, or transactions. Covers non-numeric parameters, BCL type collisions, and COM accessors that cannot be properties.
- **`.agents/rules/addin-isolation.md`** - Open when touching `InventorMcp.AddIn.Loader`, the `.addin` manifest template, the bundle layout, add-in package references, or the Roslyn scripting host, or when debugging a load context or globals cast failure.
- **`.agents/rules/build.md`** - Open before running `dotnet build`, deploying the add-in or bundle, reading logs, or driving the server by hand. Also for a file lock during a build, the local tool feed, or a client configuration entry for the server.
- **`.agents/rules/plugin-loop.md`** - Open before running or iterating on a plugin inside a live Inventor session (ex. a Design Automation plugin), loading a build output from a snippet, validating drawings against a payload set, or building an `inventor_run_plugin` tool.
- **`.agents/rules/verification-status.md`** - Open when asked what is verified or left to do, or when planning verification of a change.
