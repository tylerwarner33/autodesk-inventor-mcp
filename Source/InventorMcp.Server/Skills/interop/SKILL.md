---
name: interop
description: Facts about the Inventor COM API that make a snippet fail to compile or throw at run time - members typed as object, casts, iLogic through dynamic, E_FAIL and E_INVALIDARG causes, and collection quirks. Read it before you write an inventor_eval_csharp snippet that goes past a simple read, and after a snippet fails with a COM error.
---

# Inventor API traps for snippets

Each fact here made a real snippet fail. Confirm a member with `inventor_api_lookup` before you use it. A compile
error for a missing member carries a `HINT` with the near members: read it before you guess again.

## How a call runs

- Calls run one at a time on Inventor's main thread. A slow call can be another client's work, not a hang.
- Keep each call under 10 s. Split a loop over several calls, and guard it with `StartDeadline()`. See the
  `split-long-work` skill.
- `Document` is the target document, or null. `Application` is always there.

## Members typed as object

- `Parameter.Value` is `object`. Cast it: `(double)parameter.Value`. It is the database value (centimetres,
  radians). `Parameter._Value` is the same number as a `double`.
- Some definition members are `object`, ex. `RectangularPatternFeatureDefinition.XCount`. Cast them:
  `((Parameter)definition.XCount).Expression`. A direction that the pattern does not use gives the COM code
  `DISP_E_PARAMNOTFOUND` (-2147352572) as an `int`, not a parameter.
- A text or boolean parameter has no numeric value. Read its kind from
  `document.UnitsOfMeasure.GetTypeFromString(parameter.get_Units())`: `kTextUnits` or `kBooleanUnits`.
  Write a text or boolean parameter through `Value`, and a numeric one through `Expression`.

## Casts and conversions

- `PartComponentDefinition` and `AssemblyComponentDefinition` do not convert to `ComponentDefinition`. Read each
  member per type, ex. `document switch { PartDocument p => p.ComponentDefinition.WorkPoints, ... }`.
- `AddMateConstraint` returns `MateConstraint`. Use `var` or that type, not `AssemblyConstraint`.
- `Style.Copy` returns the base `Style`. Cast it, ex. to `TextStyle`.
- `DrawingDimension.Style` is not on the base type. Cast to the specific type, ex. `LinearGeneralDimension`.
- A face's range box is `face.Evaluator.RangeBox`. A planar face's plane is `(Plane)face.Geometry`.
- `IPictureDisp.Handle` from `Camera.CreateImage` overflows through `dynamic`. Read it by reflection as an `int`.

## Members that must be called as methods

When the COM getter and setter have different types, C# cannot form a property. The compiler error names the method.

| Member | Call |
| --- | --- |
| `Parameter.Units` | `parameter.get_Units()` |
| `iLogicAutomation.Rules` | `automation.get_Rules(document)` |
| `DrawingView.DrawingCurves` | `view.get_DrawingCurves(Type.Missing)` for all curves |

## iLogic from C#

- Use the helpers: `ILogicAutomation()`, `ILogicRuleNames`, `ILogicRuleText`, `SetILogicRuleText`,
  `RunILogicRule`. Or the tools `inventor_ilogic_rules`, `inventor_ilogic_rule_get`, `inventor_ilogic_rule_set`.
- By hand: the automation object must be `dynamic`, and the document argument of `get_Rules`, `GetRule` and
  `RunRule` must be statically typed `Inventor.Document`, not `PartDocument`. See the `ilogic` skill.

## Names that collide with System

`Inventor` is imported, so `File`, `Path`, `Environment` and others are Inventor types. Write
`System.IO.File`, `System.IO.Path` and `System.Environment`.

## E_FAIL and E_INVALIDARG

E_FAIL (0x80004005) gives no cause. `inventor_eval_csharp` adds a `CONTEXT:` line with the target document's state.
Known causes:

- A write to a document that is not modifiable: in a library path of the project, read-only, or checked in to
  Vault. `inventor_session` gives `isModifiable` and `library` for each open document. A late-bound write can give
  "Exception has been thrown by the target of an invocation" for the same cause.
- A member of a document that is closed. Read every fact you need before the first `Close`.
- A lazy LINQ query that runs after its document closed. Call `ToList()` before the close.
- `((Document)occurrence.Definition.Document).FullFileName` on some occurrences. Read
  `occurrence.ReferencedDocumentDescriptor.FullDocumentName` instead, and skip suppressed occurrences.
- A range box of a table on a sheet that is not active. Activate the sheet first, and restore the old one after.
- Closing a referenced part before its parent. Close drawings, then assemblies, then parts.
- `XDirectionEntity` of a suppressed pattern.

E_INVALIDARG causes:

- `UserParameters.AddByExpression` for a boolean. Use `AddByValue(name, true, UnitsTypeEnum.kBooleanUnits)`.
- `Documents.Add` with a template that is open.
- `AddWithOptions` with a model state name that does not exist. Read the names with
  `FileManager.GetModelStates` first.
- A Content Center part through `Occurrences.Add`. See the `projects-and-files` skill.

## Collections

- `SubOccurrences` has no `ItemByName`.
- `CompositeiMateDefinition` has `Count` and an indexer, not an `iMateDefinitions` collection.
- `ModelState.Document` is null for the active model state. Use the document itself for that state.
- `ReplaceReference` is on `FileDescriptor` (`document.File.ReferencedFileDescriptors`), and the new file must be a
  copy of the old one. `AllReferencedDocuments` leaves out suppressed components.
- A `Balloon` leader is a tree. `Leader.RootNode` is at the balloon. Each leaf node (`ChildNodes.Count == 0`) is
  an arrowhead, and its `AttachedEntity` is the `GeometryIntent` it points at.
- Two drawing views on one sheet can have the same name.

## Snippet syntax

- A returned list, dictionary, anonymous object, record or tuple of plain values comes back as JSON. Anything else
  comes back as its `ToString()`, and an Inventor object then gives only its type name (`System.__ComObject`).
  Return the values you need, ex. `new { name = part.DisplayName, dirty = part.Dirty }`.

- A top level `using var` does not compile (CS1002). Use a `using (...) { }` block.
- A `using` directive must come before any statement.
- `Microsoft.VisualBasic` is not referenced.
