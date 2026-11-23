---
name: snippets
description: The snippet host of inventor_eval_csharp - what is in scope, the helpers, what a snippet returns, the HINT and CONTEXT lines, and the syntax limits. Read it before your first inventor_eval_csharp snippet in a session, and after a snippet fails to compile or throws.
---

# Snippets

This skill covers only what this server adds around a snippet. The Inventor API facts that make most snippets fail
(members typed as `object`, casts, members called as methods, the causes of E_FAIL and E_INVALIDARG) are in the
`inventor-api-traps` skill of the `inventor-api` plugin (marketplace `tylerwarner-autodesk`). If your client has that
plugin, read that skill too. If not, the user can install it with
`claude plugin install inventor-api@tylerwarner-autodesk`.

Confirm a member with `inventor_api_lookup` before you use it.

## How a call runs

- Calls from all clients run one at a time on Inventor's main thread. A slow call can be another client's work, not a
  hang. `wallClockMilliseconds` minus `elapsedMilliseconds` is the time the call waited.
- Keep each call under 10 s. Split a loop over several calls, and guard it with `StartDeadline()`. See the
  `split-long-work` skill.
- A snippet is refused when the document has unsaved changes, unless you pass `allowUnsavedChanges`. Opening a
  generated drawing marks it as changed, so pass it when the only changes are from the open.
- `suppressRules` turns the iLogic rules off while the snippet runs. See the `ilogic-tools` skill.

## What is in scope

- `Application` is always there. `Document` is the target document, or null when no document is open and none is
  named.
- During an edit in place (ex. a part edited inside an assembly), `Document` with no `documentName` is the document in
  edit (`Application.ActiveEditDocument`), and `Application.ActiveDocument` is the top document.
- `System`, `System.Collections.Generic`, `System.Linq` and `Inventor` are imported. So `File`, `Path`, `Environment`
  and others are Inventor types. Write `System.IO.File`, `System.IO.Path` and `System.Environment`.
- `Log(...)` writes a line to the result.

## Helpers

The server adds a helper to the snippet only when the snippet calls it.

| Helper | What it does |
| --- | --- |
| `OpenOrReuseDocument(fullFileName, visible = false)` | Returns the open copy, or opens the file. Takes a full document name (the path with the model state) to open one model state. Throws, with the full document names, when two model states of one file are open. |
| `CloseDocumentsOpenedHere()` | Closes only the documents that `OpenOrReuseDocument` opened, drawings first, with no save. |
| `InTransaction(document, "MCP: Name", () => { ... })` | Runs the action as one Undo step on that document, and aborts the step when the action throws. A rule that runs inside joins the step. Put `document.Update()` inside. |
| `ILogicAutomation()`, `ILogicRuleNames(document)`, `ILogicRuleText(document, name)`, `SetILogicRuleText(document, name, text)`, `RunILogicRule(document, name)` | The iLogic automation object and its rules, with the casts done for you. They take any document type. |
| `ToInches`, `FromInches`, `ToMillimetres`, `FromMillimetres`, `ToDegrees`, `FromDegrees` | Unit conversions to and from centimetres and radians. Convert once at the top of a snippet. |
| `TryGetUserParameter(document, name, out UserParameter parameter)` | Finds a user parameter in a part, assembly or drawing, with no exception when it does not exist. |
| `StartDeadline(seconds = 8)` | A guard for a loop: `if (deadline.Passed) break;` |

## What a snippet returns

- End with an expression or a `return` statement.
- A list, dictionary, anonymous object, record or tuple of plain values comes back as JSON. Anything else comes back
  as its `ToString()`, and an Inventor object then gives only its type name (`System.__ComObject`). Return the values
  you need, ex. `new { name = part.DisplayName, dirty = part.Dirty }`.
- A result over 40,000 characters keeps its start and its end, names a file with the full text, and has
  `returnValueTruncated` or `outputTruncated`. Return less, or filter, in place of a second run.

## When a snippet fails

- A compile error for a member that does not exist carries a `HINT` with the near members of that type. Read it before
  you guess again.
- An E_FAIL gives no cause. The server adds a `CONTEXT:` line with the state of the target document (ex. whether it is
  modifiable, or in a library path).
- A failed call can still have changed the model. See the `read-back-after-failed-write` skill.

## Syntax limits

- A top level `using var` does not compile (CS1002). Use a `using (...) { }` block.
- A `using` directive must come before any statement.
- `Microsoft.VisualBasic` is not referenced.
- The snippet cannot reference the assemblies of the user's project. See the `probe-project-code` skill.
