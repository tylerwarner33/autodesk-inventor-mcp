---
name: Inventor Interop
description: Non-obvious facts about the Inventor COM interop for the add-in code, the long snippet limit, and where the snippet facts live
triggers:
  - Editing add-in code under Source/InventorMcp.AddIn/Operations
  - Writing a snippet that reads or writes Parameters, UnitsOfMeasure, HealthStatus, ErrorManager, or transactions
  - A compiler error about a COM property or an ambiguous type name
globs:
  - "Source/InventorMcp.AddIn/**"
---

## The snippet facts are in the server's skills

The API facts that a snippet needs apply to every model that uses the server, so they live in the skills that the
server serves through `inventor_skill`. Read them before you write a snippet, and change them there:

- `Source/InventorMcp.Server/Skills/interop/SKILL.md`: members typed as `object`, non-numeric parameters, casts,
  members that must be called as methods, names that collide with `System`, and the causes of E_FAIL and
  E_INVALIDARG.
- `Source/InventorMcp.Server/Skills/ilogic/SKILL.md`: iLogic through `dynamic`, triggers, `RulesEnabled`.
- `Source/InventorMcp.Server/Skills/modeling/SKILL.md`: units and the document's `UnitsOfMeasure`.
- `Source/InventorMcp.Server/Skills/drawings/SKILL.md` and `projects-and-files/SKILL.md`.

This rule keeps what only this repository's code needs.

## Facts for the add-in code

### Inventor type names collide with the BCL

`Inventor.Environment`, `Inventor.File`, `Inventor.Path` and others shadow their `System` counterparts.
Fully qualify the `System` one inside the add-in.

### Some COM members cannot be C# properties

When the COM getter and setter have different types, the language cannot form a property and the accessor must be
called directly (ex. `parameter.get_Units()`). The compiler error names the accessor to use. The skill `interop`
lists the known cases.

### Facts behind the add-in operations

- `HealthStatusEnum` has no warning member. The real members include `kCannotComputeHealth`,
	`kInconsistentHealth` and `kRedundantHealth`.
- `ErrorManager` exposes no entry collection. It offers `AllMessages` as one text blob plus `HasErrors` and
	`HasWarnings`, so there is no genuine per entry severity.
	So `inventor_health` reports which features are sick, but not why.
- `Transaction.DisplayName` carries the command name behind each modelling operation, which is what makes the
	activity feed readable.
- `PartFeature` carries no failure text, only `HealthStatus`. The server adds an explanation for a status whose
	cause is measured (`AddHealthHints`), so far only `DriverLost`.
- Assembly feature collections can hold entries that do not expose `PartFeature`; those are skipped.
- `inventor_update` marks a clean document dirty even when the rebuild changes nothing, so it is not a read only call.

## A long snippet can terminate Inventor

A snippet runs on Inventor's main thread, so no window message is processed until it returns.
On 2026-09-22 a 58 s snippet that entered and left sketch edit about 1,200 times filled the thread's message queue,
and Inventor 2025 terminated with no chance to save:

```
System.ComponentModel.Win32Exception (1816): Not enough quota is available to process this command.
   at System.Windows.Interop.HwndTarget.UpdateWindowSettings(...)
```

It shows in the Windows Application event log as a `.NET Runtime` event 1026, and `addin.log` has no
`Bridge stopping` line for that process. It is not a busy rejection:
every call was accepted.

Keep each call under about 10 s and split a loop that creates or edits sketches, views or documents over several
calls. The server appends a warning to a snippet result over 10 s (`_longExecutionWarningThreshold` in
`InventorTool.cs`), the same number as the instructions and the skill `split-long-work`. A plugin run gets no
warning, because it cannot be split.
iLogic rule chains are long in practice: on 2026-09-25 one top level `Master` rule of an automation assembly ran
79 s, and one parameter write with the rules on took 85 s, with no failure. Use `suppressRules` for a batch.
Run time is only a proxy: the risk grows with the window messages a call causes, so a short call that edits the
user interface heavily can still fill the queue.
