---
name: Inventor Interop
description: Non-obvious facts about the Inventor COM interop for the add-in code, the long snippet limit, and where the Inventor API facts and the snippet facts live
triggers:
  - Editing add-in code under Source/InventorMcp.AddIn/Operations
  - Writing a snippet that reads or writes Parameters, UnitsOfMeasure, HealthStatus, ErrorManager, or transactions
  - A compiler error about a COM property or an ambiguous type name
globs:
  - "Source/InventorMcp.AddIn/**"
---

## Where the facts live

The Inventor API facts apply to any code that drives Inventor, not only to this server, so they live in the plugins of
the [autodesk-agent-skills](https://github.com/tylerwarner33/autodesk-agent-skills) repository (marketplace
`tylerwarner-autodesk`). Install the plugins, or read the files there, before you write a snippet. Change a fact
there, with a pull request to that repository:

- `inventor-api-traps` (plugin `inventor-api`): members typed as `object`, non-numeric parameters, casts, members that
  must be called as methods, names that collide with `System`, and the causes of E_FAIL and E_INVALIDARG.
- `inventor-modeling` (plugin `inventor-api`): units, named faces, extent direction, and how to verify a write.
- `inventor-drawings` (plugin `inventor-api`): sheets, views, balloons, styles, and images.
- `inventor-ilogic` (plugin `inventor-ilogic`): iLogic through `dynamic`, triggers, `RulesEnabled`.
- `inventor-projects-and-files` (plugin `inventor-data`): projects, Vault, saved versions, model states, copies.

The facts that belong to this server stay in the skills that it serves through `inventor_skill`
(`Source/InventorMcp.Server/Skills/`): the snippet host and its helpers in `snippets`, and the tools for each topic in
`verify-a-write`, `drawing-checks`, `ilogic-tools` and `file-tools`. A skill of this server names only the API facts
that its steps need, and points to the plugin skill for the rest.

This rule keeps what only this repository's code needs.

## Facts for the add-in code

### Inventor type names collide with the BCL

`Inventor.Environment`, `Inventor.File`, `Inventor.Path` and others shadow their `System` counterparts.
Fully qualify the `System` one inside the add-in.

### Some COM members cannot be C# properties

When the COM getter and setter have different types, the language cannot form a property and the accessor must be
called directly (ex. `parameter.get_Units()`). The compiler error names the accessor to use. The skill
`inventor-api-traps` of the plugin `inventor-api` lists the known cases.

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

### Model states and the edit document

Measured in Inventor 2026 on 2026-11-19, with an assembly that has two occurrences of one part, one in the primary
model state and one in a second state. The resolver of the add-in (`DocumentNameMatch`) and `OpenOrReuseDocument`
follow these facts.

- `FullDocumentName` is the path for the primary model state, and the path with `<State>` for another state, ex.
	`C:\Parts\Bracket.ipt<State2>`. `DisplayName` is then `Bracket.ipt (State2)`.
- An open model state has two documents with the same `FullDocumentName`: the factory
	(`ComponentDefinition.IsModelStateFactory`) and the member (`IsModelStateMember`). Its factory has the active model
	state of the file. Both report `IsModifiable` true. The member of the primary state names that factory as its
	`FactoryDocument`.
- A document that was never saved has an empty `FullFileName`, and a **null** `FullDocumentName` through COM. Guard
	a `.Length` on it.
- `Application.ActiveEditDocument` is never null with a document open. With no edit in place, it is the same object as
	`ActiveDocument`. During an edit in place of a part, it is the part, and `ActiveDocument` stays the assembly. During
	a sketch edit (in a top part or in a part in place), it is the part.
- During an edit in place, `Application.ActiveView.Document` is the top document, so a camera (ex.
	`inventor_orientation`) reads the view of the assembly.

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
