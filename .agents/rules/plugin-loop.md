---
name: Plugin Development Loop
description: Running and iterating on a plugin inside a live Inventor session with inventor_run_plugin and inventor_drawing_layout
triggers:
  - Running, testing, or iterating on a plugin (ex. StrobicConfigurator) inside a live Inventor session
  - Validating generated drawings or layout values against a set of payloads
  - Editing InventorTool.PluginLoop.cs or InventorTool.DrawingLayout.cs
  - Making another plugin loop-ready
globs:
  - "Source/InventorMcp.Server/McpTools/InventorTool.PluginLoop.cs"
  - "Source/InventorMcp.Server/McpTools/InventorTool.DrawingLayout.cs"
---

## Plugin development loop

`Docs/Plugin-Development-Loop.md` is the full guide: how a call works, how to set up a new plugin, and the
StrobicConfigurator worked example with its staging, payload set and exact call. Read it before a first run.

### Use the tools, not hand-written loaders

- `inventor_run_plugin` copies the build output, loads the copy into a fresh collectible context, and calls one
	method. Arguments bind **by name**. Pass `closeDocumentsUnder` with the folder the plugin writes to.
- `inventor_drawing_layout` measures a generated drawing and flags collisions. Measure every case before calling a
	layout change done, not only the one in the request.
- Try a setting read from a file beside the assembly through `shadowFiles` first. Change the default in code only
	once the measured value is right.

### Rules

- **Never borrow an open document.** `Documents.Open` returns the user's copy if it is open. A snippet that opens a
	drawing must first check `Documents` for that path, and close only what it opened itself. Measure a copy of a file
	the user might have open.
- **Build the project whose output the loop loads**, and check the DLL time stamp moved before trusting the run.
- **One `inventor_run_plugin` call per case.** Short calls stay inside the client's wait, and each loads the newest
	build.
- **Hide documents unless the run is for watching.** Visible documents cost about twice the run time.
- **Verify the content, not only the code.** A stale part produces a drawing with no error and a missing feature.

### StrobicConfigurator specifics

- Load `Cincinnati.InventorPlugin.McpServerLoop\bin\Debug`, type `Cincinnati.InventorPlugin.McpServerLoop.EntryPoint`,
	method `Run`. Build that project: its output holds the DA project and the core too.
- Pass `logFilePath` as `%LOCALAPPDATA%\Cincinnati\Cincinnati.InventorPlugin.McpServerLoop\Logs\log-.txt`. The log is
	shared by every run, and the tool reports only the lines the call wrote.
- Stage each case with a junction to LocalDebug's `InputFiles\content`, never `Published Content (New)\content`.
- Its side of the loop is documented in its `Docs/Inventor/Development/MCP-Server-Loop.md`.
