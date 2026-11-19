# Document Targeting

Created: 2026-11-16. Plan added: 2026-11-16.

Status: **open, live checks first. The code needs an add-in build**, so it goes into the bridge version 3
release (`Docs/Tasks/Bridge-JsonRpc-Migration.md`). The analysis is in `Docs/Research/Task-Analysis.md`,
"Document targeting".

Each tool finds its document with `documentName`: empty for the active document, or a display name or a full
path. Three cases give a wrong document with no message.

## Verdict

- **Makes sense:** yes. Each gap is a write to a wrong document with no error.
- **Recommended:** yes.
- **Architecture:** the single resolver is the add-in's `ResolveDocument`. This removes the copy in the server
	snippets. It does not add a helper to the script globals, so "Script helpers travel with the snippet" holds.

## Gaps

1. **Edit in place.** When the user edits a part inside an assembly, `Application.ActiveDocument` is the assembly
	and `Application.ActiveEditDocument` is the part. Every resolver uses `ActiveDocument`.
2. **Two model states of one file.** Two open documents have the same `FullFileName` and different
	`FullDocumentName` values. Every resolver compares `FullFileName`, so the first one wins.
3. **Two documents with one display name.** The first match wins.

## Where a document is resolved today

| Where | File |
| --- | --- |
| `ResolveDocument` | `Source/InventorMcp.AddIn/Operations/InventorOperations.cs` |
| The `Document` global of a snippet (`EvaluateCSharp`) | `Source/InventorMcp.AddIn/Operations/InventorOperations.Execution.cs` |
| `FindToolDocument` | `Source/InventorMcp.Server/McpTools/InventorTool.ILogic.cs` |
| The `only` filter of `SessionStateSnippet` | `Source/InventorMcp.Server/McpTools/InventorTool.Session.cs` |
| `Application.ActiveDocument` and a `FullFileName` loop | `InventorTool.AutoBalloon.cs`, `InventorTool.DrawingLayout.cs` |
| `OpenOrReuseDocument` | `Source/InventorMcp.Server/ScriptPrelude.csx` |

## The design

- **The add-in resolves. The snippets do not.** A composed snippet sends its `documentName` in
	`ExecuteRequest.DocumentName`. The add-in already resolves that name to set the `Document` global. The snippet
	then uses `Document`. An error of the resolver is a bridge error with a code, the same for each tool.
- **The rules, in this order:**
	1. Empty name: `ActiveEditDocument`, then `ActiveDocument`, else `no-active-document`.
	2. A name equal to one `FullDocumentName` (no case): that document.
	3. A name equal to the `FullFileName` or the `DisplayName` of exactly one document: that document.
	4. A name that matches more than one document: `ambiguous-document` (new code), with the full document name of
		each match in the message and in `detail`.
	5. No match: `not-found`.
- **A file that is not open.** A tool that can open a file tries the call with the name first. On `not-found`,
	when the name is a full path of a file that exists, the server sends the snippet again with no document name
	and a preamble that opens the file with the rules off (the code that is in `FindToolDocument` today).
- **The match is a pure function** on three strings for each document, in its own file with no Inventor type, so
	the test project compiles it.

## Decisions

1. **The default document during an edit in place.** Recommended: the edit document. `inventor_session` reports
	both, and each `documentName` description says so.
2. **A path that matches two model states.** Recommended: `ambiguous-document`. The first match of today is
	arbitrary, and `Documents.Open` with a path opens the primary model state, which can be neither.

## Checks before the code

Inventor must run. Record the results in `.agents/rules/inventor-interop.md` or `Docs/Research/`, not here.

- [ ] Start an edit in place of a part in an assembly. Read `ActiveDocument`, `ActiveEditDocument`, and both
	with no edit in progress (is `ActiveEditDocument` then equal to `ActiveDocument`, or null?). Call
	`inventor_set_parameter` with no `documentName`, and record which document changed.
- [ ] The same during an edit of a sketch in the part in place, and during an edit of a sub-assembly in place.
- [ ] Open an assembly that uses two model states of one part. Read `FullFileName`, `FullDocumentName`,
	`DisplayName` and `ModelStateName` of each open document. Record the exact format of `FullDocumentName`, and if
	the document of the primary model state is always open too.
- [ ] `FullDocumentName` of a document that was never saved.
- [ ] `inventor_orientation` and `inventor_drawing_layout` during an edit in place: which document must they use?
	A camera belongs to the view of the top document.

If `ActiveEditDocument` is null with no edit in progress, rule 1 is already correct. If a check shows a rule to
be wrong, change the rule here before the code.

## Steps

### Contracts (`Source/InventorMcp.Contracts`)

1. `BridgeErrorCodes`: add `AmbiguousDocument = "ambiguous-document"`.
2. `DocumentInfo` in `Models/SessionInfo.cs`: add `FullDocumentName` (string) and `ModelStateName` (string or
	null). `SessionInfo`: add `ActiveEditDocument` (`DocumentInfo` or null), set only when it is not the active
	document. Each is a new member at the end, with a default, so the JSON of an old add-in still reads.

### Add-in (`Source/InventorMcp.AddIn`)

3. Add `Operations/DocumentNameMatch.cs`, with no Inventor type:
	`static MatchResult Find(IReadOnlyList<(string DisplayName, string FullFileName, string FullDocumentName)> documents, string name)`
	returns the index of the match, or "none", or the indexes of an ambiguous match. Rules 2 to 5.
4. `ResolveDocument` in `InventorOperations.cs`: rule 1 with `ActiveEditDocument`, then `DocumentNameMatch.Find`,
	then `BridgeFailureException` with the code.
5. `EvaluateCSharp` in `InventorOperations.Execution.cs`: use `ResolveDocument` for the empty name too, but keep
	a null `Document` when no document is open (catch `no-active-document` only).
6. `ToDocumentInfo` and `GetSession`: fill the new members. Read `ModelStateName` in a `try` block, because a
	drawing has none.
7. `OpenDocumentKeys` in `InventorOperations.Execution.cs`: use `FullDocumentName`, so two model states are two
	keys.

### Server (`Source/InventorMcp.Server`)

8. `RunJsonSnippetAsync` in `McpTools/InventorTool.ILogic.cs`: add the parameters `documentName` and
	`allowOpen`. Send `documentName` in the `ExecuteRequest`. Catch `InventorBridgeException` with `not-found`,
	and when `allowOpen` is true and `Path.IsPathFullyQualified(documentName) && File.Exists(documentName)`, send
	the snippet again with the open preamble.
9. Replace the constant `FindToolDocument` by a short constant `ToolDocument`:
	a function that returns the document that the preamble opened, or `Document`, or throws "No document is
	active". Change each caller (`InventorTool.ILogic.cs`, `Features.cs`, `Files.cs`, `SheetImage.cs`,
	`Execution.cs`) from `FindToolDocument(<literal>)` to `ToolDocument()`, and pass the name to
	`RunJsonSnippetAsync`. Keep the wrapper that calls `CloseDocumentsOpenedHere()` in a `finally` block.
	`inventor_file_info` resolves many files in one snippet: keep a loop there, and use rule 2 and 3 through
	`OpenOrReuseDocument`.
10. `OpenOrReuseDocument` in `ScriptPrelude.csx`: compare `FullDocumentName` first, then `FullFileName`. When
	more than one open document has the `FullFileName`, throw an exception that lists the full document names.
11. `SessionStateSnippet` in `InventorTool.Session.cs`: add `fullDocumentName` and `modelState` to each document,
	and `activeEditDocument` to the result. Send its `documentName` through the add-in as in step 8, in place of
	the `only` filter.
12. `inventor_auto_balloon` and `inventor_drawing_layout`: use the result of the last live check. Replace the
	`FullFileName` loop by the same path as the other tools.
13. Each `documentName` description: "A display name, a full path, or a full document name (the path with the
	model state). Omit for the active document: during an edit in place, that is the document in edit."
14. `Skills/projects-and-files/SKILL.md`: one sentence that `Documents.Open` and `OpenOrReuseDocument` take a
	full document name to open one model state. `Skills/interop/SKILL.md`: `Document` is the edit document during
	an edit in place, and `Application.ActiveDocument` is the top document. `ServerInstructions.md`: no new line,
	unless a live session shows that models write to the wrong document.

### Tests

15. Compile `DocumentNameMatch.cs` into the test project (the `Compile Include` list in
	`InventorMcp.Server.Tests.csproj`). `Unit/DocumentNameMatchTests.cs`: display name, full path, full document
	name, a path with two model states (ambiguous), two equal display names (ambiguous), a full document name
	that also is a prefix of another, no match, and a different case.
16. `BridgeClientTests.cs` or a new test with `TestPipeServer`: `RunJsonSnippetAsync` sends the name in the
	request. On a `not-found` answer with `allowOpen` and a path of a temporary file, it sends a second request
	with the open preamble. With `allowOpen` false, it does not.
17. A `Live` test: an assembly with a part in edit, and `inventor_set_parameter` with no name.

## Definition of done

- [ ] No file in `Source/InventorMcp.Server` compares `FullFileName` or `DisplayName` to find the document of a
	tool call, except `OpenOrReuseDocument`.
- [ ] `inventor_documents` shows two different rows for two model states of one file.
- [ ] An ambiguous name returns `ambiguous-document` with the names to use.
- [ ] `CHANGELOG.md`: the entries are part of the bridge version 3 section.

## When the plan is done

- Record the resolver rules in `Docs/Architecture.md`, and the API facts in `.agents/rules/inventor-interop.md`.
- Delete this document.
