---
name: Inventor Interop
description: Non-obvious facts about the Inventor COM interop - parameters, units, type collisions, and accessors
triggers:
  - Editing add-in code under Source/InventorMcp.AddIn/Operations
  - Writing a snippet that reads or writes Parameters, UnitsOfMeasure, HealthStatus, ErrorManager, or transactions
  - A compiler error about a COM property or an ambiguous type name
globs:
  - "Source/InventorMcp.AddIn/**"
---

## Interop facts learned against a live session

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

### A long snippet can terminate Inventor

A snippet runs on Inventor's main thread, so no window message is processed until it returns.
On 2026-09-22 a 58 s snippet that entered and left sketch edit about 1,200 times filled the thread's message queue,
and Inventor 2025 terminated with no chance to save:

```
System.ComponentModel.Win32Exception (1816): Not enough quota is available to process this command.
   at System.Windows.Interop.HwndTarget.UpdateWindowSettings(...)
```

It shows in the Windows Application event log as a `.NET Runtime` event 1026, and `addin.log` has no
`Bridge stopping` line for that process. It is not a busy rejection (`Docs/Tasks/Busy-Inventor-Call-Rejection.md`):
every call was accepted.

Keep each call under about 10 s and split a loop that creates or edits sketches, views or documents over several
calls. The server appends a warning to any execution result over 20 s (`_longExecutionWarningThreshold` in
`InventorTool.cs`). The value sits above the 6 to 12 s plugin runs, which were safe, so routine runs do not warn.
Run time is only a proxy: the risk grows with the window messages a call causes, so a short call that edits the
user interface heavily can still fill the queue.

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
- `inventor_update` marks a clean document dirty even when the rebuild changes nothing, so it is not a read only call.
