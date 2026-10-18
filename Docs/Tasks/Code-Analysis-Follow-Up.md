# Code Analysis Follow-Up

Created: 2026-10-18

Status: **open.** The build now enforces the analyzers and the style rules that the code obeys.
See `Docs/Architecture.md`, "The build enforces the code rules". These items make the build enforce more.
Do one item at a time, and give its rule the severity `warning` in `.editorconfig` when the code obeys it.
Delete this document when every item is done or dropped.

## Logging

- [ ] Change the 20 log calls in the server to `[LoggerMessage]` methods, which the source generator writes.
	Then remove the `suggestion` severity of CA1848 and CA1873 from `.editorconfig`.
	The calls are in `BridgeClient.cs`, `InventorTool.cs`, `ApiReferenceService.cs` and `InventorInstallations.cs`.

## Style options that the code does not obey

The owner decides each item: change the code to obey the option, or change the option to describe the code.

- [ ] IDE0055 (formatting): 1,578 findings, mostly the tab after `///` in an XML doc comment.
- [ ] IDE0290 (primary constructor): 4 classes in the add-in assign their fields in a constructor.
- [ ] IDE0046 and IDE0058: 21 findings each. Decide whether either rule is wanted at all.

## Smaller items

- [ ] MA0051 (method length over 60 lines): 16 methods, the longest `InventorTool.Start` with 163 lines.
	It is a `suggestion` now. Split a method when you change it.
- [ ] `InventorOperations.OnMainThread` returns a task but has no `Async` suffix.
- [ ] CA1859 is a `suggestion`, because the private operation methods return `IReadOnlyList<T>` on purpose.
	Confirm or change.
