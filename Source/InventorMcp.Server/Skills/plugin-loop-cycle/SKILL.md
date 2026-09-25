---
name: plugin-loop-cycle
description: The edit, build, run and measure cycle for a plugin (ex. a Design Automation plugin) inside a live Inventor session with inventor_run_plugin, with no Inventor restart per change. Read it when a user asks you to run, test or change a plugin against Inventor, or to validate generated drawings over a set of cases.
---

# Plugin loop cycle

`inventor_run_plugin` copies a build output folder, loads the copy into a new context, and calls one public method.
The build output is never locked, and each call loads the newest build. So Inventor stays open between changes.

## What the plugin needs

- A public entry point with plain parameters: an `InventorServer` or `Application` parameter (the tool fills it) and
  otherwise strings, numbers, booleans or enums. Arguments bind by parameter name.
- The working folder as a parameter. It must not change `Environment.CurrentDirectory` or environment variables:
  they are Inventor's.
- A log file that it disposes before it returns. A snippet has no console.
- No dialogs on the path that it runs: a modal dialog stops every call.
- A target framework that the Inventor release loads: `net8.0-windows` for 2025, 2026 and 2027.

The plugin's own repository documents its entry point, how to stage a case, and its log path. Read that first.

## The cycle

1. **Look up the API** with `inventor_api_lookup` before you use a member you are not sure of.
2. **Change the code and build** the project whose output the loop loads (2 to 5 s). Check that the DLL time stamp
   changed, or the run uses the old build.
3. **Run one case per call.** `inventor_run_plugin` with `buildOutputDirectory`, `assemblyFileName`, `typeName`,
   `methodName`, `arguments`, `closeDocumentsUnder` (the folder the plugin writes to) and `logFilePath`. A case took
   6 to 16 s, and 24 to 44 s with a large export. One case per call keeps each call short.
4. **Read the log result:** the counts of warnings and errors, the marked lines, and the lines that match
   `logPattern`.
5. **Measure the output**, not the return value: `inventor_drawing_layout` on each generated drawing,
   `inventor_export_sheet_image` to look at it, and `inventor_features` or `inventor_hole_check` for the models. A
   stale part gives a drawing with no error and a missing feature.
6. **Repeat for every case** before you call a change done.

## Settings with no build

A setting that the plugin reads from a file beside the assembly can be tried with `shadowFiles`: the tool writes the
file into the copy before the load. Change the default in code only when the measured value is correct.

## Documents

- Hide documents unless the user wants to watch: visible documents cost about twice the run time.
- `closeDocumentsUnder` refuses to run while a document there has unsaved changes, and closes the run's documents
  after it. Pass `keepDocumentsOpen` to measure them, then close them with `inventor_close_documents`.
