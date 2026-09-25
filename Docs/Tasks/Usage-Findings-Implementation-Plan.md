# Usage Findings Implementation Plan

Created: 2026-09-24

Status: **open.** The evidence is in `Docs/Research/Usage-Findings-And-Knowledge-Delivery.md`, below "the research".
A reference such as "R1.3" means item 3 of section 1 in the research's "Findings, by where they should go".

## Goal

- Fewer failed calls, and fewer calls that send the same snippet again after a fix.
- Fewer long calls on Inventor's main thread, mostly from iLogic triggers.
- Knowledge that reaches every client, not only agents that work on this repository.

## Decisions from the review

1. **Do the work in the server first.** Most of R1 is a canned snippet or a change to a result, and both are in the
	server. Only a small set of items needs the add-in. Phase 6 collects them into one add-in release, because each
	add-in rebuild needs Inventor closed.
2. **Try the script helpers as a server prelude first, then move them to the globals.** R1.1 puts the helpers in
	`InventorScriptGlobals`, which is in the add-in. The server can instead put a helper prelude before the snippet.
	That needs no rebuild, but the prelude compiles on each call and moves the line numbers in the diagnostics.
	Put `#line 1` after the prelude so the line numbers stay correct. Measure the compile cost against the median
	call. If the cost is too high, move the stable helpers into the globals in Phase 6.
3. **Use helper names that say what they do.** Use `ToInches(cm)` and `FromInches(inches)`, not `In(cm)`. `In` looks
	like a keyword and can collide with a name that a model uses in its own code.
4. **Put a trap for one tool in the description of that tool.** Claude Code loads MCP tool schemas only when a tool
	is needed, so a tool description uses context only when the tool is used. `ServerInstructions.md` uses context
	in every session. From R2, these go in tool descriptions or the interop skill instead of the instructions:
	the `Parameter.Value` cast, and "calls run one at a time".
5. **Use one time limit: 10 s.** Change the warning threshold in `InventorTool.cs` from 20 s to 10 s for
	`inventor_eval_csharp` and `inventor_run_ilogic`. Do not add the warning to `inventor_run_plugin`, because a
	plugin run cannot be split (R1.7).
6. **Find a modal dialog from outside Inventor's process.** While a dialog blocks the main thread, no bridge call
	can reply. The server can find the dialog with Win32: Inventor's main window is disabled
	(`IsWindowEnabled` is false), and a top level window of the same process is enabled and has a title.
	This works while the main thread is blocked. Verify it with the iLogic Security Alert, which may not be a
	standard dialog class. Tested on 2026-09-24 with a real iLogic error dialog: see
	`Docs/Research/Blocking-Dialog-Detection.md` for the method, the proposal to detect and close information
	dialogs during the wait, and `Docs/Tasks/Server-Test-Project-Plan.md` for the tests.
7. **Serve the skills as the research recommends.** Put them in `Source/InventorMcp.Server/Skills/<name>/SKILL.md`,
	embed them, and serve them through `inventor_skill` until the C# SDK and the clients support the extension.
	`McpServerSkill.Create` computes the manifest from bytes, so embedded files will also work with the SDK later.
8. **The skills own the Inventor API facts.** The facts in `.agents/rules/inventor-interop.md` and
	`inventor-modeling.md` that apply to snippets move to the skills. The rules keep the facts about this repository
	(ex. the add-in load context) and point to the skills for the rest. This prevents two copies that drift apart.
9. **`suppressRules` changes a setting for the whole session.** `RulesEnabled` applies to all documents, not to one
	batch. Always restore it in `finally`. Find out if the setting stays after an Inventor restart. If it stays, an
	Inventor crash during a batch leaves the rules off, and `inventor_session` must report that.
10. **A rule text write can cause a Security Alert.** The research found that a rule edit through the API can make
	iLogic block the next run with a dialog. Test `inventor_ilogic_rule_set` for this before it ships. If the alert
	occurs, the result and the description of the tool must say so.

## Phases

Each phase can ship alone. The order is by value and by restart cost.
Before a build, open `.agents/rules/build.md`. Verify each item against a live session, then check
`inventor_health` after each write.

### Phase 1 - Quick wins in the server

No add-in rebuild. Files: `McpTools/InventorTool.cs`, `McpTools/InventorTool.Execution.cs`,
`McpTools/InventorTool.PluginLoop.cs`, `McpTools/InventorTool.Model.cs`, `McpTools/InventorTool.Session.cs`,
`Services/ApiReferenceService.cs`, `Program.cs`.

- [x] Remove CS8632 from the diagnostics of a successful result (R1.2).
- [x] On CS1061 or CS0117, read the type and the member from the diagnostic. Add up to 5 near matches for that type
	from `ApiReferenceService` (R1.2). Added `FindNear`. It also names the other types that have a member with that
	name, nearest type name first, with Proxy types left out.
- [x] Write the error code of each tool result to the server log (R1.13). `SafeAsync` has `error` and
	`ExecutionResult.ErrorType`.
- [x] Keep logs by day, not by file count (R1.13). Use `retainedFileTimeLimit` and set `retainedFileCountLimit`
	to null. 14 days.
- [x] Add the wall clock time to the execution results. The difference from `ElapsedMilliseconds` is the time in
	the queue and the time to open a drawing (R1.7, R1.8). `ExecutionResult.WallClockMilliseconds`, set by the
	server only, so the add-in needs no rebuild.
- [x] Use one time limit (decision 5).
- [x] `inventor_run_plugin`: return the lines that have a warning or error marker, with a cap. Add an optional
	`logPattern` (R1.7). Also `logMatchLines`, default 50.
- [x] `inventor_health`: add `includeSuppressed`, with false as the default. Filter in the server on
	`FeatureHealth.IsSuppressed` (R1.9).
- [x] `inventor_assembly_tree`: add a summary mode (the count at each depth, and the unique documents) and a limit
	on the output size with a message (R1.10). The limit is 60,000 characters.
- [x] In the description of `inventor_eval_csharp`, say that opening a generated drawing marks it dirty
	(R1.11).

Verify: send again some failed snippets from the research (ex. `face.RangeBox`), and check that the near matches
show the correct member.

Verified on 2026-09-25 against Inventor 2026 through the packed server: the `Face.RangeBox` hint, the removed
CS8632, `wallClockMilliseconds`, and the result log line. `Face.RangeBox` has no near member on `Face`, and 157
other types have `RangeBox`, so the hint can only point at some of them and at `inventor_api_lookup`. The hint for
`CenterlineTypeEnum.kWorkFeatureCenterline` names `kWorkFeatureCenterlineType` (unit test). The changed
`inventor_run_plugin` snippet compiled in Inventor. Not verified live: its marked lines and `logPattern` against a
real plugin log.

### Phase 2 - Script helpers

Decisions 2 and 3. Files: a new embedded prelude in `Source/InventorMcp.Server/`, and
`McpTools/InventorTool.Execution.cs`.

- [x] Documents: open by path or use the open copy, and record which documents the snippet opened. A second
	helper closes only those documents, in the close order of `inventor_run_plugin`. The same order, in the prelude,
	because the plugin snippet is a separate composition.
- [x] iLogic: get the automation object, list and read rules, set rule text and run a rule. Each takes any document
	type and does the `(Document)` cast and the `dynamic` call inside.
- [x] Units: `ToInches`, `FromInches`, `ToMillimetres`, `FromMillimetres`, and degrees and radians.
- [x] Parameters: `TryGetUserParameter(document, name, out parameter)`.
- [x] Time guard: a deadline object that a loop can check (ex. `if (deadline.Passed) break;`).
- [x] Put `#line 1` after the prelude, and verify that the line numbers in a diagnostic match the snippet. The
	snippet's leading `using` directives go before the prelude, and `#line` gives the next line its own number.
- [x] Measure the compile time with and without the prelude. Write the result in `Docs/Architecture.md`.
- [x] List the helpers in a short paragraph in the description of `inventor_eval_csharp`.

Verify: write a few model snippets from `executed-code.log` again with the helpers, and compare the results.

Verified on 2026-09-25 against Inventor 2026 through the packed server: every helper, the diagnostic line number
after a moved `using` directive, and the compile cost. The prelude is sent only with a snippet that calls a helper.
Decision 10: an API edit of a saved rule, then a run, opened no Security Alert (a part in a temporary folder, with
this machine's iLogic security options). The cause of the alert in the research is not known yet.

### Phase 3 - iLogic tools

R1.3 and R1.5. Canned snippets through `BridgeOperations.EvalCSharp` that use the Phase 2 iLogic helper.
New file: `McpTools/InventorTool.ILogic.cs`.

- [x] `inventor_ilogic_rules`: the name, the active flag and the length of each rule, for an open document or a
	path.
- [x] `inventor_ilogic_rule_get`: one rule, or all rules written to files in a folder.
- [x] `inventor_ilogic_rule_set`: insert or replace at an anchor that must occur exactly once. Stop if it does not.
	Make CRLF line ends the same, write a backup of the old text under `%LOCALAPPDATA%\InventorMcp\`, and return a
	diff. See decision 10. It also refuses to write when the rule changed between its read and its write, and a
	file that is not open needs `save=true`.
- [x] `inventor_run_ilogic`: accept a document path and a rule name. A rule name runs that rule, in place of a
	temporary rule body. This part is a snippet in the server. Nothing can stop a call on the main thread, so a
	timeout can only be reported. It cannot be enforced. A write tool takes an open document only (display name
	or full path), because a change to a document that the call opens is lost when it closes.
- [x] `inventor_set_parameters`: a bulk write of names and expressions, with `suppressRules`, `runRuleAfter` and
	`createIfMissing` (R1.5). See decision 9.
- [x] `suppressRules` on `inventor_set_parameter` and `inventor_eval_csharp`. With the option, the server sends a
	snippet in place of the add-in operation. For `inventor_eval_csharp` it is three calls (off, the snippet, restore
	in the server's `finally`), because a wrapping `try` block would break a snippet that declares methods.

Verify on test copies of a template with many rule triggers, never on the masters:

- A bulk write with `suppressRules` takes much less time than one write per parameter with the rules on.
- The rule runs once after the batch.
- `RulesEnabled` has its old value after the call and after a failed call.
- `inventor_ilogic_rule_set` causes a Security Alert, or it does not.

Verified on 2026-09-25 on Inventor 2025 (the user changed from 2026 during the work), on a test copy of
`C:\Work\Designs\Frame\Master Frame.iam` in
`C:\Work\_McpTest\Frame`. The copy has the 29 model files of the master
folder, made with Apprentice `FileSaveAs` so no rule ran. Its references to the project's library paths (`Designs`
and the Content Center, 28 files) point at the originals, which the project makes read-only. The masters were not
changed. Delete the `_McpTest` folder when the tests are done, and do not check it into Vault.

| Measurement | Result |
| --- | --- |
| One parameter write with the rules on (the old way) | 85 s |
| Two writes with `suppressRules` | 73 ms, then `RulesEnabled` was true again |
| One run of the top `Master` rule | 79 s |
| Four writes with `suppressRules` and `runRuleAfter: Master` | 59 ms of writes, 67 s in total |

- `RulesEnabled` was true after the batch, after a snippet that threw with `suppressRules`, and after the rule run.
- A text parameter, a boolean parameter and a missing name (reported per parameter, no exception) in one batch.
- `inventor_ilogic_rule_set` refused an anchor that occurs 3 times, and inserted at a unique one with a backup and a
	diff. The edited rule then ran by name through `inventor_run_ilogic` in 120 ms. No Security Alert opened. So
	decision 10 is not reproduced: two API edits, one of a saved rule, opened no alert on this machine.
- Opening the test copy took 4.7 s and made it dirty, as R1.11 found for drawings.

Not verified: whether `RulesEnabled = false` stays after an Inventor restart (decision 9). The server always
restores it, and says so when the restore fails.

### Phase 4 - Session state and read tools

R1.2, R1.4, R1.6, R1.8 and R1.12. Canned snippets, and Win32 calls in the server.

- [ ] `inventor_session`: add the active `.ipj`, its workspace and its library paths, and `IsModifiable` for each
	open document. Add the modal dialog check from decision 6. If a dialog is open, send no bridge call. Report the
	dialog title.
- [ ] `inventor_eval_csharp`: on `E_FAIL`, add `IsModifiable` of the target and the project data from the step
	above (R1.2).
- [ ] `inventor_close_documents(under)`: close the documents under a folder, in the close order of
	`inventor_run_plugin`. Refuse a dirty document unless the caller allows it. Never close a document outside the
	folder.
- [ ] `inventor_features`: the name, type, suppressed flag and health of each feature. For a pattern, also the
	count and spacing expressions, the direction entity and the parent features.
- [ ] `inventor_pattern_elements`: the transform of each element.
- [ ] `inventor_hole_check`: the holes by radius, with their positions, to verify a cut.
- [ ] `inventor_drawing_layout`: add the arrowhead checks and the leader tip check, and the annotation extents for
	each view with the gaps between view groups (R1.8).
- [ ] `inventor_file_info(paths)`: iProperties for each model state, the saved version, model state names, and
	work point and iMate names, for many paths, with paging. Close each document that the tool opened.
- [ ] `inventor_export_sheet_image`: a sheet, or a region of it, as an image. Return it as MCP image content, so the
	model can look at it with no PDF renderer. Use `inventor_api_lookup` to find the export member first.
- [ ] `inventor_test_copy`: copy a document tree with a prefix, clear the read-only attribute and repoint the
	references.
- [ ] `inventor_styles`: the styles of a document, with a diff against a second document.

### Phase 5 - Knowledge delivery

R2, R3 and R4, and decisions 4, 7 and 8. Do this phase after Phases 1 to 4, so the skills name the new tools in
place of hand-written snippets.

- [ ] Write the topic skills: `interop`, `ilogic`, `projects-and-files`, `drawings`, `modeling`.
- [ ] Write the workflow skills: `safe-template-edit`, `ilogic-rule-edit`, `plugin-loop-cycle`,
	`read-back-after-failed-write`, `split-long-work`, `probe-project-code`.
- [ ] Each skill has `name` and `description` frontmatter, and `name` is the same as its folder name. Embed the
	folder with a `LogicalName` that keeps the path.
- [ ] Add `inventor_skill` with `list` (name, description, files) and `read` (one file).
- [ ] Change `.agents/rules/inventor-interop.md` and `inventor-modeling.md` so they point to the skills for the
	snippet facts (decision 8).
- [ ] Change `ServerInstructions.md` once, with all the lines from R2 that stay after decision 4, and one line that
	names the skills and says when to read each one.

Verify: in Claude Desktop and in Visual Studio, outside this repository, give a task that needs a skill. Check
that the model calls `inventor_skill` before it writes a snippet.

### Phase 6 - One add-in release

Inventor must be closed for this phase, so do all of these items together. First, open
`.agents/rules/addin-isolation.md` and `.agents/rules/inventor-interop.md`.

- [ ] `executed-code.log`: write the outcome, the duration and the client name of each snippet (R1.13). The server
	gets the client name from the MCP `initialize` request and sends it in `ExecuteRequest`. This is a change to
	`InventorMcp.Contracts`, so the server and the add-in must ship together.
- [ ] `addin.log`: write handler failures (R1.13).
- [ ] `addin.log`: limit how often it writes `All pipe instances are busy`, so that a flood of lines cannot fill
	the log.
- [ ] After a failed snippet, list the documents that it created (R1.2). Compare the open documents before and
	after the snippet.
- [ ] Move the stable helpers to `InventorScriptGlobals` if the Phase 2 measurement shows it is necessary.

### Phase 7 - Skills over MCP

Start when `ModelContextProtocol.Extensions.Skills` (csharp-sdk#1864, or the pull request that replaces it) has a
release.

- [ ] Serve the same folder with the extension, through `McpServerSkill.Create` over the embedded files, or
	`WithSkillsFromDirectory`.
- [ ] Keep `inventor_skill` until the clients that the team uses are on the client matrix. Then remove it, and
	remove its line from `ServerInstructions.md`.

## Not in this plan

- A reference to a project's assemblies from a snippet (R1.1). `inventor_run_plugin` does this job now.
- A `cases[]` batch mode for `inventor_run_plugin` (R1.7). The queue time from Phase 1 will show if it is necessary.
- Ignoring a document that is dirty only because it was opened or updated (R1.11). Phase 1 documents this instead.
- The ideas in R6. Examine the server side check after every write again after Phase 5.

## Coverage of the research

| Research | Phase |
| --- | --- |
| R1.1 script helpers | 2, and 6 if measured |
| R1.2 better errors | 1, 4, 6 |
| R1.3 iLogic tools | 3 |
| R1.4 session state | 4 |
| R1.5 parameters | 3 |
| R1.6 feature and geometry tools | 4 |
| R1.7 `inventor_run_plugin` | 1 |
| R1.8 `inventor_drawing_layout` | 1, 4 |
| R1.9 `inventor_health` | 1 |
| R1.10 `inventor_assembly_tree` | 1 |
| R1.11 `unsaved-changes` guard | 1 |
| R1.12 new tools | 4 |
| R1.13 logging | 1, 6 |
| R2 server instructions | 5 |
| R3 guide content | 5 |
| R4 workflows | 5 |
| R5 documentation cleanups | Done with this plan |
| R6 earlier ideas | Not in this plan |

## When the plan is done

- Record the results in `.agents/rules/verification-status.md`.
- Add these decisions to `Docs/Architecture.md`: the helper prelude or globals, tool descriptions against server
	instructions, the delivery of the skills, and the dialog check from outside Inventor's process.
- Keep the research in `Docs/Research/`. Delete this document.
