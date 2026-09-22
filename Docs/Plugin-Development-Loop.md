# Plugin Development Loop

How to run a plugin you are developing inside a live Inventor session, change its code, and run it again, with no
Inventor restart per change.

It is two MCP tools plus a small contract the plugin keeps:

| Tool | Job |
| --- | --- |
| `inventor_run_plugin` | Copy a build output, load the copy into a fresh context, and call one method on it |
| `inventor_drawing_layout` | Measure a drawing's views, balloons, dimensions and tables, and report collisions |

The reference plugin is `C:\repos\Cincinnati\StrobicConfigurator`. How it uses the loop is in
[Worked example: StrobicConfigurator](#worked-example-strobicconfigurator). How to make another plugin ready is in
[Setting up a new plugin](#setting-up-a-new-plugin).

## Why

Inventor loads an add-in once and never releases the file. Its assembly load context is not collectible, and
unloading the add-in in the Add-In Manager runs `Deactivate` but keeps the file locked. So a plugin developed the
normal way costs a CAD restart per code change.

The loop never lets Inventor load the plugin. Inventor loads only this repository's bridge add-in. The bridge compiles
a snippet, and the snippet loads the plugin from a copy, into a context that can be thrown away.

| | Inventor's own loading | The loop |
| --- | --- | --- |
| Where the binaries live | A plugin folder, or wherever the `.addin` manifest points | `bin\`, untouched. A copy is what loads. |
| What is registered | A manifest and a class id | Nothing |
| What starts it | Inventor startup, or the Add-In Manager | An `inventor_run_plugin` call |
| Entry point | `ApplicationAddInServer.Activate` | Any public method |
| Load context | One, for the life of the process | A new collectible one per call |
| Cost of a code change | Close Inventor, rebuild, reopen | `dotnet build`, then call again |

The two can run side by side. An installed copy of the same plugin stays in its own context and does not collide
with the loop's copy, but their log lines mix, so turn off "Load Automatically" for it while you loop.

## How a call works

1. **Copy.** The build output folder is copied, recursively, to
	`%TEMP%\InventorMcp\plugin-loop\<assembly>\<timestamp>`. Only the copy is loaded, so `bin\` is never locked and
	`dotnet build` can run while Inventor is open. Copies older than a day are removed on later calls.
2. **Replace files, optionally.** `shadowFiles` writes files into the copy before loading, ex. an `appsettings.json`
	with trial values. That changes configuration with no build.
3. **Load.** The copy is loaded into a new collectible `AssemblyLoadContext`. Its `Load` override resolves every
	dependency from the copy first. Only `Autodesk.Inventor.Interop` is left to Inventor's own copy, so Inventor's
	types stay one type on both sides.
4. **Bind by name.** A parameter typed `InventorServer` or `Application` receives the session. Every other parameter
	takes the value named in `arguments`, or its default. An unknown name, a missing value, or more than one matching
	overload is refused, with the signatures listed. Position never matters.
5. **Call.** For an instance method, the object is built with a constructor that takes only an `InventorServer`, an
	`Application` or defaults. A plugin exception is reported with its own type and message.
6. **Clean up.** The context is unloaded. The lines the plugin wrote to its log during the call, when a log is given,
	are summarised. With `closeDocumentsUnder`, the documents the run opened there are closed, unless
	`keepDocumentsOpen` is set.

`closeDocumentsUnder` also protects your work: the call refuses to run while any document under that folder has
unsaved changes, and it closes the others before running. Documents are closed drawings first, then assemblies,
then parts, because closing one can close others.

### Why a new context per call

- The build output is never locked, because only a copy is loaded.
- New code always wins, because each call has its own copy of every type.
- Static state starts clean, so one run cannot inherit a stale handle from another.
- Inventor's non-collectible context holds none of the code that changes.

`Unload` is best effort. A static that still holds a COM object, or an Inventor event handler the plugin subscribed,
keeps a context alive. A leaked context costs memory. A leaked event handler also keeps firing, so a plugin must
release its handlers.

## Setting up a new plugin

The loop needs no change in this repository for a new plugin. It needs a plugin that keeps six rules.

1. **A public entry point with plain parameters.** A public static method, or an instance method on a class with a
	simple constructor. Give it an `InventorServer` parameter and otherwise strings, numbers, booleans or enums.
	The tool cannot pass objects. A plugin that needs a payload object should take the file path and read it.
2. **Take the working folder as a parameter.** Never set `Environment.CurrentDirectory`, an environment variable or
	`Console.Out`. Inventor is the process, so those changes reach every other add-in. If the plugin must set one,
	restore it in a `finally`.
3. **Log to a file, and dispose the logger before returning.** A snippet has no console. Give the file to the tool
	as `logFilePath`. See [The plugin's log](#the-plugins-log).
4. **No dialogs on the path it runs.** A modal dialog owns Inventor's message pump, and the bridge posts its work to
	that pump, so an unanswered dialog stalls every tool. Take every answer as a parameter.
5. **A target framework the running Inventor can load.** `net8.0-windows` loads on Inventor 2025, 2026 and 2027.
	`net10.0-windows` loads on 2027 only.
6. **Keep the entry point's signature stable.** The tool binds by name at run time, so a renamed parameter still
	builds and fails only when the loop runs. Add a new method rather than change one.

The interop can be referenced either way. With `EmbedInteropTypes`, the plugin never loads
`Autodesk.Inventor.Interop` itself and its interfaces match Inventor's by GUID, so one build works on every release.
Without it, keep the reference `Private=false`, so the copy never carries its own interop.

A minimal entry point:

```csharp
public static class EntryPoint
{
	public static void Run(InventorServer inventorServer, string workingDirectoryPath, bool showDocuments)
	{
		using StreamWriter log = new(@"C:\Work\Logs\loop.log", append: true) { AutoFlush = true };
		// ... build, draw, export, writing progress to 'log' ...
	}
}
```

And the call that runs it:

```json
{
	"buildOutputDirectory": "C:\\repos\\MyPlugin\\Source\\MyPlugin\\bin\\Debug",
	"assemblyFileName": "MyPlugin.dll",
	"typeName": "MyPlugin.EntryPoint",
	"methodName": "Run",
	"arguments": { "workingDirectoryPath": "C:\\Work\\Case1", "showDocuments": false },
	"closeDocumentsUnder": "C:\\Work\\Case1",
	"logFilePath": "C:\\Work\\Logs\\loop.log"
}
```

### The plugin's log

The tool marks each log file before the call and reads back **only what the call wrote**. So one log can hold every
run as history, and each call still reports on its own run:

- **Any logger works.** A file that grows, a file overwritten on each run, and a rolling log all report correctly.
	The tool compares the file's last bytes from before the call to tell an append from a rewrite.
- **A rolling log is given by its base path.** Serilog's `...\Logs\log-.txt` matches `log-*.txt`, so a run that
	crosses midnight into `log-20260923.txt` is still read whole. A matching file the call does not write adds nothing.
- **Environment variables are expanded**, so a per user folder can be named as `%LOCALAPPDATA%\...`.
- **Warnings and errors are counted by text.** The defaults match both plain loggers and Serilog's default template:
	`WARN` and `[WRN]`, and `ERROR`, `FATAL`, `[ERR]` and `[FTL]`. Pass `warningMarkers` and `errorMarkers` for any
	other format.
- **The result stays small.** It carries the counts and the last `logTailLines` lines of this run, however large the
	file grows. The history stays on disk, to search when it is needed.

Dispose the logger before the method returns. The tool reads the file after the call, and an open sink can also keep
the collectible context alive. Put a header line at the start of each run, with the case and the build time stamps,
so a shared log reads as a timeline and a stale build shows as a repeated time stamp.

### Hidden or visible documents

A plugin that creates documents should let the caller choose visibility. Visible documents cost about twice the run
time: nine StrobicConfigurator payloads took 162 s visible and 79 s hidden in the same kind of session. Hide them
unless the point of the run is to watch it.

## The iteration

1. Look the API up with `inventor_api_lookup` before writing code against a member you are not sure of.
2. Change the plugin and run `dotnet build` on the project whose output you load. Inventor stays open.
3. Call `inventor_run_plugin` once per case. One call per case keeps each call short, and each gets the newest build.
4. Check the result against the model, not the return value:
	- `inventor_drawing_layout` on each generated drawing, for collisions and spacing
	- `inventor_eval_csharp` to read the model itself, ex. which work points or model states a part has
	- `inventor_health` after a write, for sick features
5. Repeat from step 2.

`inventor_eval_csharp` runs with no document open, so none of this needs a document open first.

A setting read from a file beside the assembly can be tried with no build at all: pass the file through
`shadowFiles`, measure, and change the default in the code only once the value is right.

## Worked example: StrobicConfigurator

StrobicConfigurator builds Inventor assemblies, drawings and exports from a configuration payload. Its Design
Automation path is what the loop runs.

### What it provides

The entry point is `Cincinnati.InventorPlugin.McpServerLoop.EntryPoint`, in its own project beside the DA project:

```csharp
public static void Run(InventorServer inventorServer, string workingDirectoryPath, bool exportStep, bool exportRfa, bool exportPdf, bool showDocuments)
```

It keeps every rule above:

- It is a separate project so it can log through Serilog like StrobicConfigurator's other local hosts. The DA
	project must not reference Serilog, because its whole output is copied into the Design Automation app bundle.
- The working folder goes to an `internal` `PluginAutomation` constructor, reached through `InternalsVisibleTo`. It is
	internal because `PluginAutomation` is `[ComVisible]` and Design Automation calls its `Run` by name through COM late
	binding, so a public overload would change what the engine sees. Design Automation never reaches the loop project.
- The log is a daily rolling file in `%LOCALAPPDATA%\Cincinnati\Cincinnati.InventorPlugin.McpServerLoop\Logs\`,
	the same pattern as the add-in's and LocalDebug's, kept for 60 files. Each run starts with a header: the case, the
	exports, and the time stamp of each of the three DLLs.
- `InventorGlobals.InventorServer`, the `AppLogger` instance and `DAS_WORKITEM_ID` are restored in a `finally`.
- `showDocuments` sets `DAS_WORKITEM_ID` for a hidden run, which is the only thing that variable controls.
- It references the interop with `EmbedInteropTypes`, so the 2025 build runs on 2026 and 2027 as well.

`Cincinnati.APS.LocalDebug` is unchanged and remains the command line loop. The two are separate workflows.

### Staging

One working folder per payload, the same layout as a Design Automation work item:

```
<case>\
	content\       a directory junction to Cincinnati.APS.LocalDebug\InputFiles\content
	payload.json   from the case folder in Downloads\Cincinnati\Published Content (New)
	rules.xml      from Cincinnati.APS.LocalDebug\InputFiles
```

A junction rather than a copy, because the plugin only reads `content\` and builds in its own `output\` copy.

**Use LocalDebug's content folder.** `Published Content (New)\content` held the Sep 1 versions of 21 of its 110 parts
on 2026-09-22, without the `PlenumInlet_*` work points. A run from it succeeds and silently drops every inlet
dimension.

### The payload set

Nine payloads cover the configurations: QUO001-1_B/_S (1X4, one row), TS1_B/_S (1X1), TS4_B/_S and TS4.5_B (2X3, two
nozzle rows), TS5_B/_S (1X3). `_B` is a bottom inlet and `_S` a side inlet. StrobicConfigurator's
`Docs/Inventor/Development/Drawing-Validation-Workflow.md` describes what each one exercises.

### A run

```json
{
	"buildOutputDirectory": "C:\\repos\\Cincinnati\\StrobicConfigurator\\Source\\Desktop\\Inventor\\Cincinnati.InventorPlugin.McpServerLoop\\bin\\Debug",
	"assemblyFileName": "Cincinnati.InventorPlugin.McpServerLoop.dll",
	"typeName": "Cincinnati.InventorPlugin.McpServerLoop.EntryPoint",
	"methodName": "Run",
	"arguments": {
		"workingDirectoryPath": "<case folder>",
		"exportStep": false,
		"exportRfa": false,
		"exportPdf": true,
		"showDocuments": false
	},
	"closeDocumentsUnder": "<case folder>",
	"logFilePath": "%LOCALAPPDATA%\\Cincinnati\\Cincinnati.InventorPlugin.McpServerLoop\\Logs\\log-.txt"
}
```

A case takes 4 to 10 s hidden. The output is `<case>\output.pdf` and the built files in `<case>\output\`. Copy the PDF
to `Downloads\Cincinnati\<yyyy-MM-dd>_Outputs\<case> output.pdf` to compare it with earlier runs.

Build `Cincinnati.InventorPlugin.McpServerLoop` after a change in the DA project or the core; its output holds all
three. Confirm the core DLL's time stamp moved before trusting a run, because a build that decides nothing changed is
quick and silent. The run header in the log records it.

### Trying layout values without a build

`PluginAutomation.ReadDrawingLayoutOptions` reads the `DrawingLayout` section of the `appsettings.json` beside its own
assembly on every run. So a trial value needs no build:

```json
"shadowFiles": { "appsettings.json": "{ \"DrawingLayout\": { \"BalloonSpacingInches\": 0.3 } }" }
```

Then measure every case with `inventor_drawing_layout`, and change the default in `DrawingLayoutOptions.cs` once the
value is right. `BalloonSpacingInches` went from 0.4 to 0.3 this way on 2026-09-22.

## Measuring drawings

`inventor_drawing_layout` reports in inches from the sheet's lower left corner:

- the sheet, border and every view
- every dimension's text box, and every parts list, custom table and title block
- balloons grouped by the view their leader reaches, with the closest centre spacing and the group's extent

It flags as `ISSUE`: overlapping balloons, crossing leaders, a balloon or dimension overlapping another annotation or
a table, and anything outside the border.

A balloon has no range box in the API. Its centre is `Balloon.Position`. A style that scales to its text has no
stored diameter, so it is estimated at 3 times the text height. That matched the rendered PDF for STROBIC's style,
0.24 in circles on 0.08 in text. Pass `balloonDiameterInches` when another style needs a better value.

The tool is read only. A drawing it opens is closed without saving, and a drawing already open is measured and left
as it was.

## Rules learned the hard way

- **Never borrow a document the user has open.** `Documents.Open` returns the already open copy. A snippet that then
	closes "its" document closes the user's, with their unsaved changes. Check `Documents` for the path first, and
	close only what the snippet opened itself.
- **Close drawings, then assemblies, then parts, and list the documents again before each close.** `Close` on a
	document an earlier close already took throws `E_FAIL`. `Documents.CloseAll(UnreferencedOnly: true)` also threw
	`E_FAIL` with an unsaved document open.
- **Match Inventor types by name when binding.** A plugin built with embedded interop types has its own copy of
	`InventorServer`, which COM treats as the same type and .NET does not.
- **Override `Load`, not the `Resolving` event.** `Resolving` runs only after the default context fails, so a
	dependency another add-in already loaded would win. It is the same trap as the Roslyn globals cast in
	`Architecture.md`.
- **Verify the content, not only the code.** Stale parts produce a drawing with no error and a missing feature. Read
	the model before blaming the code.
- **A long call can outlast the MCP client's wait.** The run in Inventor continues either way, and the plugin's own
	log is the record. One call per case avoids it.

## What the loop cannot cover

- Ribbon definitions, commands, and anything that depends on the add-in being registered.
- `Activate` and `Deactivate`, and any loader shim Inventor instantiates by class id.
- Manifest discovery and the bundle layout: a wrong `<Assembly>` or a stale DLL at the bundle root passes the loop and
	fails on install.
- Interactive dialogs.
- For a Design Automation plugin, the engine itself: no `ActiveDocument`, its own working directory, and inputs the
	engine stages. Confirm against a real work item before release.

Those need one manual pass with the plugin installed. When the hosts are thin and the core does the work, that pass
is short.

## Verified

On Inventor 2025.4, .NET 8.0.30, on 2026-09-22, through the tools themselves:

- **`inventor_run_plugin`** ran StrobicConfigurator's nine payloads, one call per case, 6.3 to 9.9 s each. Each call
	bound `inventorServer` to the session and the other five arguments by name, guarded and closed the case's
	documents, and returned the plugin log's line, WARN and ERROR counts.
- **`inventor_drawing_layout`** measured all nine generated drawings: **0 issues** each. Every front view group
	packs at 0.30 in (7 to 9 balloons, 1.80 to 2.40 in), and every side view carries its 2 balloons.
- The DA project was rebuilt repeatedly with Inventor running, and the loop's copy ran beside the installed
	StrobicConfigurator add-in.
- `inventor_eval_csharp` runs with no document open.

Not yet run: Inventor 2027. StrobicConfigurator's embedded interop types should make its 2025 build bind there too.

Later on 2026-09-22, eleven live calls through StrobicConfigurator's `Cincinnati.InventorPlugin.McpServerLoop`
project, 5.6 to 14.3 s each, with `logFilePath` given as the rolling base path `log-.txt`. Each call reported only
the lines it wrote to the shared `log-20260922.txt` (323 to 541 lines), with its own warning and error counts. The
log helpers were also run outside Inventor against a new file, an append, a rewrite shorter and longer than before, a
midnight roll, an untouched matching file, and a file a writer still held open. All ten cases passed.
