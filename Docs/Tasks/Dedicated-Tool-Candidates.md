# Dedicated Tool Candidates

Created: 2026-11-16. Plan added: 2026-11-16.

Status: **open, decisions recommended.** Server only: each tool here is a composed snippet. The analysis and the
counts are in `Docs/Research/Task-Analysis.md`, "Dedicated tool candidates".

The server has a few dedicated tools and one general tool, `inventor_eval_csharp`, for everything else. This
document lists work that has no dedicated tool, and decides which rows become a tool.

## Verdict

- **Makes sense:** for 2 of the 14 rows.
- **Recommended:** build `inventor_selection` and `inventor_save`. Add one prelude helper for a PDF export. Make
	the other rows skill examples, and only when a session needs one.
- **Architecture:** a tool for each row goes against "Execution rather than a tool per operation". The usage
	research found that models almost never call the dedicated read tools, so a new tool must pass a test below.

## When a dedicated tool is better than a snippet

- **It is read-only.** A client can run a tool with `ReadOnly = true` with less confirmation. A snippet is
	arbitrary code.
- **It has a guard that a snippet forgets.**
- **It has a trap.** The model fails on its first try.
- **It is frequent.** A short tool call costs less than a snippet, and its result has a stable shape.

A dedicated tool costs a description in each session, and the server already has 35 tools. So a candidate that
passes none of the four tests stays a snippet, with an example in a skill.

## Decisions

Counts are snippets in the audit logs that match a text pattern, of 4,437. Each is an upper limit.

| Work | Snippets | Tests it passes | Recommended |
| --- | --- | --- | --- |
| Read the user's selection | 8 | Read-only. No other way exists to know what the user points at | **Tool: `inventor_selection`** |
| Save a document, with the release guard | 436 | Guard, frequent. A save in a newer release cannot be undone | **Tool: `inventor_save`** |
| Export a document to PDF | 46, 7 in failed snippets | Trap (the translator add-in and its options) | **Prelude helper `ExportPdf`** |
| Open a document | 2,615 | Frequent | Nothing. `OpenOrReuseDocument` exists |
| Find objects by type and name | 1,415 | Read-only | Nothing. `inventor_features` and `inventor_assembly_tree` cover it |
| Occurrence state: suppress, visible, grounded | 163 | Frequent, no trap | Example in a new skill `assemblies` |
| Place a component | 35 | Trap, with many options | Example in the skill `assemblies` |
| Representations and model states | 86 | Read-only for the list | Example in the skill. `inventor_file_info` reads them |
| Set the camera | 72 | | Example in the `modeling` skill |
| iPart and iAssembly tables | 9 | Trap, rare | Later, when a session needs it |
| Material and appearance search | 5 | Read-only, rare | Later |
| Document settings | 4 | Read-only, rare | Later |
| iLogic event triggers | 1 | Trap, rare | Later, in the `ilogic` skill |
| Delete a parameter or a property | 1 | Guard, rare | Later |

## Patterns for a tool that changes several items

- Take a list of items, and return one result for each item with the counts (requested, succeeded, failed). The
	model then repeats only the failed items. See `Docs/Architecture.md`, "Safety", for when such a result is an
	error.
- One Undo step for the whole call. See `Docs/Architecture.md`, "Parameter writes are one Undo step".
- The same `documentName` rules as each other tool. See `Docs/Tasks/Document-Targeting.md`.

## Plan for `inventor_selection`

Do it after `Docs/Tasks/Document-Targeting.md`, because both use the edit document. It can also be done before,
with `Application.ActiveDocument`, and changed later.

1. Live checks first, with snippets:
	- [ ] During an edit of a part in place in an assembly, select a face. Which `SelectSet` has it: that of the
		top document, or that of the part? Which type has the item (ex. `FaceProxy`)?
	- [ ] The item types for: a face, an edge, a feature in the browser, an occurrence, a sketch entity, a
		drawing view, a drawing curve segment, a dimension, a balloon.
	- [ ] `Application.CommandManager.Pick` is a different thing (it asks the user). Do not use it.
2. Add `Source/InventorMcp.Server/McpTools/InventorTool.Selection.cs`:
	- `[McpServerTool(Name = "inventor_selection", ReadOnly = true, OpenWorld = false)]`.
	- Parameters: `documentName` (optional), `maxItems` (default 50).
	- A composed snippet through `RunJsonSnippetAsync`, inside `SafeAsync`. It reads `document.SelectSet`, and for
		each item returns: `index`, `objectType` (the `Type` member as an `ObjectTypeEnum` name), `name` when the
		object has one, the owner document (full document name), the occurrence path for a proxy, and key values
		for the common types:

		| Type | Values |
		| --- | --- |
		| A face | Surface type, area, and for a plane the normal as a ViewCube face name where it matches one |
		| An edge | Curve type, length |
		| A feature | Name, feature type, suppressed, health |
		| An occurrence | Name, referenced file, suppressed, grounded |
		| A drawing view | Name, scale, referenced file |
		| Other | Only the type and the name |

	- Read each value in its own `try` block, so one item that fails does not fail the list.
	- The result has `count`, `items`, `truncated`, and the document that was read.
3. The description says: "Reads what the user selected in Inventor. Call it when the request says 'this', 'the
	selected', or 'here'." Add one line to `ServerInstructions.md` only if a live session shows that a model does
	not call the tool for such a request, because that text is in every session.
4. A snippet that needs the selected object itself reads `Document.SelectSet[index]`. Say so in the description,
	with the index from the result.
5. Tests: a unit test that the composed snippet has each argument as a C# literal. A `Live` test: select a face
	with `SelectSet.Select`, call the tool, and check the type.
6. Add the row to the tool table in `README.md`. `ToolPipelineTests.ToolListIsTheSameAsTheSnapshot`
	covers the hints.

## Plan for `inventor_save`

1. Read how `inventor_file_info` gets the saved release of a file (`InventorTool.Files.cs`), and use the same
	code. Live check first:
	- [ ] Which member gives the release that a file was last saved in, for an open document? Compare it with the
		value of `inventor_file_info` for a file saved in an older release and opened, not yet saved, in a newer one.
2. Add the tool to `InventorTool.Files.cs`:
	- `[McpServerTool(Name = "inventor_save", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]`.
	- Parameters: `documentName` (optional), `allowReleaseChange` (default false), `includeReferenced`
		(default false).
	- A composed snippet that, before it saves:
		- Refuses a document that was never saved (`FullFileName` is empty): "Use `SaveAs` in a snippet, with a
			path that the user gave."
		- Refuses a document that is not modifiable, with `isModifiable` and the library, as `inventor_session`
			reports them.
		- Lists each document that the save writes: the document, and each dirty referenced document. With
			`includeReferenced` false and a dirty referenced document, it refuses and returns the list, so the model
			asks the user.
		- For each document of the list, compares the saved release with the release of the running Inventor. When
			the file is of an older release and `allowReleaseChange` is false, it refuses with
			`release-change`, the two releases, and the text: "A file saved in this release does not open in the older
			one. Ask the user."
	- Then `document.Save()` or `Save2(true)`, and a result with each saved file and its release before and after.
3. Change the line about saving in `ServerInstructions.md` to name the tool. This removes a rule that the model
	had to follow by hand. Visual Studio asks the user to trust the server again when this text changes, so put
	the change in the same release as other changes of that file.
4. Update `Skills/projects-and-files/SKILL.md`.
5. Tests: a unit test of the composed snippet, and a `Live` test with a new part: a save, a second save
	(idempotent), and a refusal for a document that was never saved.

## Plan for `ExportPdf`

1. Add `ExportPdf(object drawing, string fullFileName, bool allSheets = true)` to `ScriptPrelude.csx` and to
	`ScriptPrelude.HelperNames`. Confirm each member with `inventor_api_lookup` first (the translator add-in, its
	options map, and the data medium). It refuses a path that exists.
2. Add it to the helper list in the description of `inventor_eval_csharp` and to `Skills/drawings/SKILL.md`.
3. A `Live` test with a new drawing.

## Plan for the skill `assemblies`

1. Add `Source/InventorMcp.Server/Skills/assemblies/SKILL.md` in the Agent Skills format, as the other skills:
	occurrence state for a list of occurrences (read `Suppressed` first), place a component with a model state
	(`AddWithOptions` and a full document name), the Content Center trap from the usage research, and
	representations.
2. Run each example in a live session before it is written. An example that was not run is a new trap.
3. Add the skill name to the line in `ServerInstructions.md` that lists the skills, and to `SkillCatalogTests.cs`
	if it lists the names.

## Phases

- [x] Count in the audit logs how often each row was done with a snippet. The counts are in
	`Docs/Research/Task-Analysis.md`.
- [ ] Confirm the decisions.
- [ ] `inventor_save`.
- [ ] `inventor_selection`.
- [ ] `ExportPdf` and the skill `assemblies`.

Each is one pull request with an `Added` entry in `CHANGELOG.md` (a minor version). See
`.agents/rules/changelog.md`.

## When the plan is done

- Update the tool table in `README.md`.
- Delete this document.
