# Usage Findings and Knowledge Delivery

Created: 2026-09-24

Status: **research.** Real usage of the server from 2026-09-21 to 2026-09-24 found traps, missing tools and
knowledge that does not reach the clients. This document records the findings, where each one should go, and how
the Skills over MCP extension fits. The work that came from it is done, and its checks are in
`.agents/rules/verification-status.md`. The rest is in `Docs/Tasks/Skills-Over-Mcp-Plan.md`.

## Source of the findings

- 758 Inventor tool calls in Claude Code sessions, in three repositories: this repository (180), a project that
	edits an iLogic template library (345) and a plugin project that uses the plugin loop (232).
- `%LOCALAPPDATA%\InventorMcp\executed-code.log`: 691 snippets (16, 264, 328 and 83 per day; 685 C#, 6 iLogic).
	204 of them are the server's own snippets: 121 from `inventor_run_plugin` and 83 from `inventor_drawing_layout`.
	So 487 were written by a model (median 15 lines, p90 49, max 245). 528 of the 691 ran with no target document,
	because the snippet opens its own files.
- `server-*.log` for 2026-09-23 and 2026-09-24 (322 `tools/call`), and `addin.log`.
- The plugin project's own loop document, which was found accurate. It records the per case run times
	(6 to 10 s with hidden documents).

### MCP clients

Request lines in the server logs, by MCP client:

| MCP client | Lines |
| --- | --- |
| Claude Code | 632 |
| Visual Studio | 61 |
| Claude Desktop chat (`claude-ai`) | 3 |
| Claude Desktop agent mode | 3 |
| Health probe | 3 |

## What the usage shows

1. **`inventor_eval_csharp` gets almost all the traffic.** Calls per tool:

	| Tool | Calls |
	| --- | --- |
	| `inventor_eval_csharp` | 444 |
	| `inventor_run_plugin` | 121 |
	| `inventor_drawing_layout` | 85 |
	| `inventor_api_lookup` | 44 |
	| `inventor_session` | 27 |
	| `inventor_documents` | 13 |
	| `inventor_health` | 6 |
	| `inventor_parameters` | 6 |
	| `inventor_start` | 5 |
	| `inventor_run_ilogic` | 3 |
	| `inventor_assembly_tree` | 2 |
	| `inventor_orientation` | 1 |

	The dedicated read tools are almost never used. `inventor_parameters` already reports values in document units,
	but models converted by hand instead. So a better `inventor_eval_csharp` helps more than more text.

2. **About 5 % of calls failed, and the same traps came back.** `inventor_api_lookup` ran 44 times for 487 snippets
	written by a model, and usually only after a compile failed.

3. **Knowledge in `.agents/rules/` does not reach the users of the server.** The `get_Rules(Document)` trap is
	documented in `inventor-interop.md`, but that file loads only for agents that work on this repository.
	The sessions in the template library project hit the trap 6 more times.

4. **General Inventor facts were learned in one project, and stay there.** Examples: the `.ipj` library path lock,
	file version forward compatibility, and the style library override. A session in another project cannot find
	them.

5. **iLogic triggers were the largest time cost.** The templates have a top level rule that lists many parameters
	as triggers, so each parameter change runs the full rule chain again. The three longest calls:
	- 314 s: `RunRule` of the top level rule on a template part.
	- 556 s: a loop that copied 23 user parameters into a copy. Each `Expression` write ran the rule chain again.
	- 633 s: `RunRule` of the top level rule and `Update2` on a part.

	A single parameter write took 5 to 15 s, and 2 writes in one call took 38 s. One explicit run of the top level
	rule takes about 5 s. `RulesOnEventsEnabled = false` did not help. `RulesEnabled = false` did: a write then took 19 ms.

### Durations from the server logs

| Tool | Calls | Median | p90 | Max | Over 20 s |
| --- | --- | --- | --- | --- | --- |
| `inventor_eval_csharp` | 222 | 0.29 s | 7.0 s | 633.7 s | 6 (12 over 10 s) |
| `inventor_run_plugin` | 21 | 12.8 s | 24.7 s | 44.6 s | 3 |
| `inventor_drawing_layout` | 18 | 0.66 s | 1.0 s | 1.1 s | 0 |
| `inventor_api_lookup` | 22 | 0.01 s | 0.36 s | 0.55 s | 0 |
| `inventor_start` | 1 | 91 s | | | 1 |

The other tools finish in under 3 s. No timeout fired. The "about 10 s" rule is not followed in practice for iLogic
work. In the transcripts, 4 calls went to the client's background after more than 120 s. One of them waited in the
queue behind another Claude Code session's work on the same Inventor. `inventor_start` took 34 s, 83 s and 92 s
in three starts, and one `inventor_session` took 50 s while the user's own session was busy.

What worked: 28 snippets had their own `Stopwatch` guard that stopped a loop after 8 s.

### Failure classes

| Class | Count | Example |
| --- | --- | --- |
| Wrong member or type name (compile) | about 32 | CS1061 on `Balloon.RangeBox`, `GeneralDimension.RangeBox`, `DimensionStyle.ExtensionLineGap`, `PartFeatures.CutFeatures`, `PartsListStyle.HeadingTextStyle`, `LeaderStyle.TextStyle`, `WorkPoint.Suppressed`, `Parameter.Driven`, `AllLeafOccurrences` on the definition; CS0117 on `kWorkFeatureCenterline`; CS0234 on `Microsoft.VisualBasic` |
| Cast or type (compile) | about 11 | CS0019 `object / double` from `Parameter.Value`; CS0266 `MateConstraint` to `AssemblyConstraint`; `Style.Copy` returns `Style`; CS1002 on a top level `using var` |
| iLogic through `dynamic` | 9 | `auto.Rules(doc)`; `get_Rules` or `GetRule` with a `PartDocument` argument; CS1061 on `object` when the automation object is not `dynamic` |
| COM `E_FAIL` | 12 | Writes to a document that is not modifiable; `((Document)o.Definition.Document).FullFileName` on some occurrences; `XDirectionEntity` on a suppressed pattern; closing a referenced part before its parent |
| COM `E_INVALIDARG` | 6 | Boolean `AddByExpression`; Content Center part through `Occurrences.Add`; unknown model state name in `AddWithOptions`; `Documents.Add` with a template that is open |
| `NullReference`, `TargetInvocation`, `ArgumentOutOfRange` | 7 | Mostly on suppressed occurrences or features |
| `inventor-not-running` | 8 | Inventor closed between turns |
| No active document | 5 | `inventor_run_ilogic`, `inventor_orientation` |
| `unsaved-changes` refusal | 3 | Opening a generated drawing marks it dirty, so the next call was refused until `allowUnsavedChanges` was passed |
| Plugin signature mismatch | 1 | "No 'Run' overload matches" |
| Globals cast across load contexts | 1 | During add-in development |
| Call blocked by a modal dialog | 1 | iLogic "Security Alert" after a rule edit through the API |
| Output over the token limit | 2 | `inventor_assembly_tree` at depth 3 gave 106 K characters; one snippet gave 50 K |
| Noise in a successful result | 12 | CS8632 nullable warnings |
| Main thread warning (over 20 s) | 9 | 3 of them on plugin runs that cannot be split |

81 pairs of consecutive snippets in `executed-code.log` are near copies (similarity over 0.8). Many are a fix and a
resend:

- `HolePlacementDefinition` to `SketchHolePlacementDefinition`
- `GeneralDimension` to `LinearGeneralDimension`
- a `PlanarFace` cast to `Face` and `(Plane)face.Geometry`
- `face.RangeBox` to `face.Evaluator.RangeBox`
- `Parameter.Value` to `(double)p.Value`
- `auto.Rules(doc)` to `auto.get_Rules(doc)`
- `d.Style` through `InvokeMember` after `dynamic` failed
- `Documents.Open` swapped with `ItemByName` 8 times, because the model did not know whether the file was open

The rest are the same snippet sent again for each case folder, which is a batching pattern.

### Other log facts

- `addin.log` has no exceptions, HRESULTs or handler failures, because handler failures are not written there.
- 466,356 of its lines are `Pipe accept failed: All pipe instances are busy`, all from 2026-09-23 13:29:13Z to
	13:33:32Z (about 1,800 lines per second). That was the test with more than four connections, before the fix.
- There are 23 bridge starts. Two have no `Bridge stopping` line, so Inventor terminated: 2026-09-22 15:49Z (the
	Win32Exception 1816 crash in `inventor-interop.md`) and 2026-09-23 14:48Z.
- 5 `The bridge connection dropped. Reconnecting once` warnings (`IOException`, "Pipe is broken"). Each came after
	an Inventor restart, and each reconnected.
- No `inventor-busy` result was seen.

## Findings, by where they should go

### 1. Tool changes

These have the highest value, because they work for every model. Text does not reach a model that never calls the
tool (see the Visual Studio GPT-5.3-Codex result in `verification-status.md`).

Ordered by value.

1. **`inventor_eval_csharp` helpers in the script globals.** The recurring idioms in the 487 model snippets:
	- `Documents.Open` 206 times (143 invisible), `ItemByName` 140, `FullFileName` loops 128, `Close` 138,
		`CloseAll(false)` 28. Add a helper that opens a document by path, or reuses the open copy, and remembers
		whether the snippet opened it. The `documentName` input of the tools works only for documents that are
		already open.
	- The iLogic add-in GUID written out 81 times, `get_Rules` or `GetRule` 63, `RunRule` 31, rule `Text` edits 23.
		Add an iLogic helper that takes any document type and does the `(Document)` cast.
	- A manual `/ 2.54` 154 times, against `ConvertUnits` 6 times. Add `In(cm)`, `Mm(cm)` and their inverses.
	- Existence checks for user parameters 44 times (`try { ups[n] } catch`). Add a `TryGet` helper.
	- `StringBuilder` 225, `try`/`catch` 132, `Stopwatch` 39, `dynamic` 110. A small output and time guard helper
		would shorten every snippet.
	- Optionally, a way to reference a project's assemblies from a snippet. Today a model must build a scratch
		entry point and call it through `inventor_run_plugin` to call project code.
2. **Better errors from `inventor_eval_csharp`.**
	- On CS1061, add the `inventor_api_lookup` near matches for that type to the diagnostics.
	- On `E_FAIL` from a write, check `IsModifiable`, and report the active `.ipj` and its library paths. In one
		session it took 4 calls to find that every `Expression` write failed because of the project.
	- After a failed snippet, list the documents it created. Two failed snippets left a new unsaved assembly open.
	- Remove CS8632 nullable warnings from the diagnostics of a successful result.
3. **iLogic tools.**
	- `inventor_ilogic_rules`: list the rules of a document (name, active flag, length).
	- `inventor_ilogic_rule_get`: read one rule, or write all rules to files.
	- `inventor_ilogic_rule_set`: insert or replace at an anchor that must occur exactly once. It handles CRLF,
		keeps a backup of the old text and returns a diff. Rule text was dumped and compared by hand 5 or more times.
	- Let `inventor_run_ilogic` take a document path, and run a named rule with a timeout. It failed with no active
		document, so the model stopped using it.
	- A `suppressRules` option on `inventor_set_parameter`, `inventor_eval_csharp` and a bulk parameter write. It
		sets `iLogicAutomation.RulesEnabled = false` for the batch and restores it in `finally`.
4. **`inventor_session` reports more state.**
	- The active `.ipj`, its workspace and its library paths (`DesignProjectManager` was read by hand 14 times).
	- `IsModifiable` for each open document (`IsReadOnly` or `IsModifiable` was checked by hand 36 times).
	- Whether a modal dialog is open, so a blocked call can be explained.
5. **`inventor_parameters`.** Add an `add_or_update` option, so a model does not have to check that a user
	parameter exists first.
6. **Feature and geometry tools.** Snippets filtered health by hand 54 times, walked occurrences 47 times, read
	`PatternElements` transforms 28 times, scanned cylindrical faces 28 times to prove that holes exist, and read
	`RangeBox` 32 times.
	- `inventor_features`: name, type, suppressed flag, health and, for a pattern, the count and spacing
		expressions, the direction entity and the parent features.
	- `inventor_pattern_elements`: the transform of each element.
	- A geometry check that counts holes by radius and gives their positions, to verify a cut.
7. **`inventor_run_plugin`.**
	- Return the log lines that carry a warning or error marker, with a cap. Today it returns only the count and the
		tail, so the model ran 24 separate greps on the log. Optionally accept a `logPattern`, because the model often
		wanted other lines of the plugin's own log too.
	- Do not add the main thread warning to a plugin run. A run cannot be split, and runs with a large export took
		24 to 44 s.
	- Report the time a call waited in the queue. Nine parallel calls ran one after the other, and the last result
		came about 2 min later.
	- Consider a `cases[]` batch mode.
8. **`inventor_drawing_layout`.**
	- Add checks for arrowhead to arrowhead spacing, arrowhead to another leader, and a leader tip that lands on a
		component other than the one its balloon names. On one case it reported 0 issues, but the user found three
		ambiguous balloons. The plugin's own log then measured 0.06 in between arrowheads, and 0.00 in from an
		arrowhead to another component. The model had to render the PDF to find this.
	- Report the union of annotation extents for each view, and the gaps between view groups. The model measured a
		spacing rule by hand in 9 snippets.
	- Report the time to open the drawing. `elapsedMilliseconds` was 0.5 to 1 s, but the first result took 10 to
		17 s on the wall clock. A drawing that was already open returned in 1.4 s.
9. **`inventor_health`.** Leave out features that are suppressed on purpose, or add `includeSuppressed`. One part
	listed about 15.
10. **`inventor_assembly_tree`.** Add a summary mode or paging, and optionally iProperties and constraint health.
	It was called only twice, and one call went over the token limit.
11. **The `unsaved-changes` guard.** Opening a generated drawing marks it dirty, so the guard refused the next call.
	Either document this, or do not count a document that is dirty only from an open or an update.
12. **New tools.**
	- `inventor_close_documents(under)`: close the documents under a folder in the correct order, with the close
		order of `inventor_run_plugin`. `Documents.CloseAll` was called 14 times in one session. It closes the user's
		documents too, and it still left 14 hidden case documents open. Later that session had more than 200 of the
		user's documents open with one assembly dirty. The model correctly asked the user before it closed anything.
	- A batch file information reader: iProperties per model state, last saved version (Design Tracking pid 67),
		model state names, work point and iMate names, for many paths with no need to keep them open. About 13
		paged snippets did this by hand, and the model also read the saved version outside Inventor with a Python
		library.
	- `inventor_styles`, with a diff of two documents. A reflection dump for this wrote 8,800 lines per file.
	- A test copy tool: copy a document tree with a new prefix and repoint its references. This was done by hand
		6 times with `File.Copy`, clearing the read-only attribute, and `ReplaceReference`.
	- A sheet to image export, so a drawing can be checked visually with no PDF renderer outside Inventor.
		A Python PDF library was used 15 or more times for this, with a clip region at 220 to 600 dpi.
13. **Logging.**
	- Record the outcome and the duration of each snippet in `executed-code.log`. Today it has only the time, the
		tool and the document, not the calling client.
	- `SafeAsync` returns errors as normal payloads, so every server log line says `IsError = false` and the logs
		cannot count failures. Log the error code of the result.
	- `retainedFileCountLimit: 7` counts one file per process. With many clients, most of 2026-09-23 and all of
		2026-09-21 and 2026-09-22 were already deleted. Retain by day, not by file.
	- Write handler failures to `addin.log`.

### 2. Server instruction additions

`ServerInstructions.md` reaches every client, in every session, so keep it to the traps that happen often.
Visual Studio asks the user to trust the server again each time this text changes, so make the changes in one
release, not one at a time.

Proposed lines:

- Changing a parameter can run iLogic rules again (ex. a top level rule that lists many parameters as triggers). To batch writes, set
	`iLogicAutomation.RulesEnabled = false`, restore it in `finally`, then run the rule once.
	`RulesOnEventsEnabled` does not stop parameter change runs.
- A write that fails with `E_FAIL` usually means the document is not modifiable. Check `IsModifiable` and the
	active project: a file under an `.ipj` library path is read-only by design.
- Close only the documents you opened. Never call `Documents.CloseAll`.
- Before `Save` or `SaveAs`, compare the file's saved version with the Inventor version that will read it next.
	A file saved in a newer release, even a newer point release, does not open in an older one. In one session a
	template was saved in a newer point release with no check, and it could no longer be used by the older
	release that reads it.
- Never answer a question dialog on the user's behalf, ex. an iLogic Security Alert. The server closes an
	information dialog that has only `OK`, and gives its text in the result. Changed on 2026-09-25, see
	`Docs/Research/Blocking-Dialog-Detection.md`, "Decision: close information dialogs, default on".
- Calls run one at a time on Inventor's main thread. A slow call can be another client's work.
- `Parameter.Value` and some definition members (ex. `RectangularPatternFeatureDefinition.XCount`) are typed as
	`object`. Cast them (ex. `((Parameter)d.XCount).Expression`).
- Use one time limit. Today the instructions say about 10 s and the server warning says 20 s.

### 3. On-demand guide content

This is longer material that a model needs only for some tasks. Each topic loads only when it applies, like the
`.agents/rules/` pattern, but for the users of the server. See "The Skills over MCP extension" for how to serve it.

- **interop**: the content of `inventor-interop.md` that applies to snippets, plus:
	- `AddMateConstraint` returns `MateConstraint`. Use `var` or a cast.
	- `AssemblyComponentDefinition` does not convert implicitly to `ComponentDefinition`.
	- `Style.Copy` returns the base `Style`. Cast it (ex. to `TextStyle`).
	- The iLogic `get_Rules` and `GetRule` argument must be statically typed `Inventor.Document`, and the automation
		object must be `dynamic`.
	- Read an occurrence's file with `ReferencedDocumentDescriptor.FullDocumentName`, not through
		`Definition.Document`, which can give `E_FAIL`. Guard suppressed occurrences and suppressed patterns.
	- `ReplaceReference` is on `DocumentDescriptor.ReferencedFileDescriptor`.
	- `CompositeiMateDefinition` has `Count` and an indexer, not an `iMateDefinitions` collection.
	- `SubOccurrences` has no `ItemByName`.
	- A boolean user parameter needs `AddByValue(name, true, kBooleanUnits)`. `AddByExpression` gives `E_INVALIDARG`.
	- `DrawingDimension.Style` is not on the base interface. Cast to the specific type (ex. `LinearGeneralDimension`).
	- `PartsListStyle` uses `ColumnHeaderTextStyle`.
	- A face range box is `face.Evaluator.RangeBox`. A planar face's plane is `(Plane)face.Geometry`.
	- A top level `using var` does not compile in a snippet (CS1002).
	- Close a parent before the parts it references. Closing a referenced part first gives `E_FAIL`.
	- `Documents.Add` with a template that is open gives `E_INVALIDARG`. So does `AddWithOptions` with a model state
		name that does not exist. Read the names first with `FileManager.GetModelStates`.
- **ilogic**:
	- Parameter change triggers, the measured times, and batching with `RulesEnabled`.
	- A rule without `Sub Main` cannot declare functions. In a rule with `Sub Main`, insert code inside it.
	- Editing rule text through the API can make iLogic block the rule with a Security Alert on the next run.
		The call then waits until the user clicks.
	- Opening an assembly can run on-open rules: a call can report `E_FAIL` while the file opened and became dirty.
		Check `inventor_documents` before a retry.
	- A rule can raise a modal dialog in a partial test setup, which blocks Inventor.
- **projects-and-files**:
	- The `.ipj` library path lock. The Vault read-only flag and the library path are two separate layers, and
		clearing one leaves the same error. A sibling editing project with only a workspace path is the usual way
		to edit library files.
	- Forward compatibility of saved versions, and how to read the saved version (pid 67, `SoftwareVersionSaved`).
		Files at or below the reader's build open. Files above it do not, even across point releases.
	- A "Data Format Has Changed" dialog migrates the file when you click OK. Click Cancel for a file that an older
		release must read.
	- A Content Center part does not insert by file path (`Occurrences.Add` gives `E_INVALIDARG`,
		`AddByComponentDefinition` gives `E_FAIL`). Use the Content Center API.
	- `MemberEditScope` is stored in each file, not in the session. A factory write with `kEditAllMembers`
		overwrote 333 member Part Numbers in one part.
	- A file with only `[Primary]` has `IsModelStateMember` false and no `FactoryDocument`.
	- Writes to a member document are allowed, and `SaveAs` on an assembly also saves the changed member parts.
	- A STEP file wraps long `PRODUCT(...)` lines. A regular expression over the text must allow new lines.
	- An add-in DLL that Inventor has loaded is locked, so a full build fails with MSB3061 on the copy step while
		Inventor runs. That is not a compile failure. Build to another `OutDir`, or build the other projects only.
- **drawings**:
	- A read-only style library replaces a template's layer styles at `Documents.Add`, so template edits may not
		reach the output, and a host with no active project gives different results. A probe showed that a change to
		a template's local text style did survive `Documents.Add`, so the override is not the same for every style.
	- `DrawingView.Position` moves the view and carries its balloons and dimension text. A rescale after
		annotation strands them. Fit once, before annotation.
	- There is no OLE object API. An OLE table in a template cannot be read or deleted.
	- `DrawingCurve` has no visible or hidden flag.
	- The API cannot export a `.styxml` file. Only the Styles Editor can.
- **modeling**: the content of `inventor-modeling.md`, plus:
	- Dimension sketch geometry from a projected work plane, not from a body vertex or a face edge. It stays stable
		when the geometry changes. (A user correction.)
	- Base a pattern direction on a work axis, not on a feature edge. If the feature that owns the edge is
		suppressed, the pattern fails with it.
	- A flush constraint with an offset can solve on either side. Test mate against flush and the sign of the offset,
		then check the resulting position.

### 4. Workflows

These are procedures of several steps that worked well. Each one is a candidate for a skill (see below).

- **Safe template edit.** Make test copies with a prefix, so references do not resolve to the masters. Clear the
	read-only attribute and repoint the references. Take a baseline measurement. Test with `StartTransaction` and
	`Abort`. Change one parameter per call. Validate at several input values. Then change the masters, and open them
	visibly for the user's review and check-in. Never check files in or out of Vault for the user.
- **iLogic rule edit.** Back up the rule text, use anchors that must match exactly once, stop if they do not,
	compare the text before and after, then run the rule once with triggers off.
- **Plugin loop cycle.** Edit, build the project the loop loads (2 to 5 s), check the build time stamp, one
	`inventor_run_plugin` per case (6 to 16 s, or 24 to 44 s with a large export), `inventor_drawing_layout` on every
	case, then a visual check of the output. There were no client timeouts and no stale DLL runs.
	`Docs/Plugin-Development-Loop.md` already has most of this.
- **Read back after a failed write.** Read the state before a retry, because a failed call can still change the
	model.
- **Split long work.** One parameter per call, a `Stopwatch` guard in each loop, and paging with `Skip` and `Take`
	when a read covers many files.
- **Probe project code.** `inventor_eval_csharp` cannot reference a project's assemblies, so build a scratch entry
	point and call it through `inventor_run_plugin`.

### 5. Documentation cleanups in this repository

- `AGENTS.md` points to `Docs/Tasks/` for outstanding work. Until this document, that folder was empty.

### 6. Ideas raised before and not built

From earlier sessions in this repository:

- A server side check after every write (`Docs/Architecture.md`). Not built, because current models call
	`inventor_health`. The GPT-5.3-Codex result is a reason to look at it again.
- Versioned pipe aliases for releases side by side.
- Compressing the API XML in the package.
- `.github/copilot-instructions.md` (set aside).
- A shared team feed and a release pipeline (not now). A team feed is also the route by which the skills below
	reach other team members.
- A signature check before `inventor_run_plugin` runs, which named binding made unnecessary.
- The open items in `verification-status.md` that are marked "Not yet verified" or "Not yet exercised".
- A question in an earlier session: whether `inventor_session` is "a slash command or a skill with dynamic
	loading". That is the gap that skills over MCP fill.

### Not for this repository

A client project's own details stay in that project's repository: its `.ipj` names, its rule names and parameter
conventions, its deployment settings, the defects in its CAD content, and the status of its jobs and bugs.

## Repository conventions that shape the answer

- Knowledge a model needs to use the tools travels with the server, in `ServerInstructions.md`, and must reach
	every client. It was tested from outside this repository.
- Rules that cost context in every session stay out of it. They load on demand (`AGENTS.md` and `.agents/rules/`,
	which are tool neutral and not in the automatically loaded `.claude/rules/`).
- `Docs/` is for permanent documentation. `Docs/Research/` keeps the evidence that tasks and decisions come from.
	`Docs/Tasks/` is for action items and is deleted when done.
- Code remarks say what a change needs, plus a pointer. Dates, measurements and history go in `Docs/` or the rules.
- Nothing specific to one client project goes in this repository.
- Paths use variables such as `%LOCALAPPDATA%`, not a user's own path.
- Client configuration is one machine wide entry per client, not a file per repository.

## Current server surface

- 18 tools, in `Source/InventorMcp.Server/McpTools/` (`InventorTool.cs` holds `SafeAsync` and the 20 s warning).
- **No MCP prompts and no MCP resources.** `Program.cs` registers only `WithStdioServerTransport()` and
	`WithToolsFromAssembly()`.
- `ServerInstructions.md` is an embedded resource. `Program.cs` reads it at start and sends it as
	`ServerInstructions` in the `initialize` response.
- `ModelContextProtocol` 2.0.0, which already serves base protocol `2026-07-28` requests (see the
	`inventor_start` section of `Docs/Architecture.md`).

## The Skills over MCP extension

### What it is

[Skills over MCP](https://github.com/modelcontextprotocol/ext-skills) (`io.modelcontextprotocol/skills`,
SEP-2640, final on 2026-09-13) lets a server publish skills in the
[Agent Skills](https://agentskills.io) format. The source of truth is `specification/stable/skills.mdx` in that
repository.

- **Format.** A skill is a directory with a `SKILL.md` that starts with `name` and `description` frontmatter, plus
	any supporting files. The extension is only a transport binding: the file format belongs to the Agent Skills
	specification. So content written in that format today does not need a rewrite later. Only the way it is served
	changes.
- **Addresses.** Each file is a resource at `skill://<skill-path>/<file-path>`. The last segment of the skill path
	must equal `name` (ex. `skill://inventor/drawings/SKILL.md` for a skill named `drawings`).
- **Discovery.** The server declares the extension in its capabilities and must declare `resources`.
	`skills/list` returns each skill's frontmatter and a manifest of its files, each with a SHA-256 digest and a
	size. `skills/get` returns one skill. Both carry `ttlMs` and `cacheScope`. An empty list does not prove that
	there are no skills.
- **Dynamic skills.** A skill can give `"resources": "dynamic"` in place of a manifest, for content made at run
	time. It then has no integrity check, and a client may refuse to load it.
- **Limits.** At most 512 files and 16 MiB per skill.
- **Directories.** A server that declares `directoryRead: true` must answer `resources/directory/read` for every
	skill directory.
- **Progressive loading.** The client reads the list only. It reads `SKILL.md` when the skill is activated, and
	each supporting file only when it is needed. Reading `SKILL.md` alone does not activate a skill. The client
	verifies each file against the manifest and the frontmatter against the entry, and does not use content that
	does not match.
- **Nested skills.** A skill directory can hold nested skills. They are supporting content, and each needs its own
	approval.
- **Base protocol.** `2026-07-28` or later.
- **Security, for the client.**
	- Treat the content as untrusted, as with any text from a server.
	- Get the user's approval per skill, and bind that approval to the file digests. A change to the files revokes
		it.
	- Show which server the skill came from, and keep names separate per server, so a server cannot shadow another
		skill.
	- Ignore frontmatter that widens permissions (ex. `allowed-tools`) unless the user approves it.
	- Keep cached skill content out of the folders where the client finds local skills.
	- A digest proves the content did not change. It does not prove the content is safe, because the same server
		supplies both.

Servers that implement it: Hugging Face `hf-mcp-server` (TypeScript), `github-stars-contrib-mcp-server` (Python)
and a `github-mcp-server` prototype (Go).

### How it overlaps with the findings

| Finding | Covered by the extension? |
| --- | --- |
| 1. Tool changes | **No.** A skill is text. It cannot add a helper to `inventor_eval_csharp`, return log lines, or detect a dialog. |
| 2. Server instruction additions | **No.** The extension does not replace server instructions. Traps that apply to every call must still be in the text that every client gets at start. |
| 3. On-demand guide content | **Yes, directly.** This is what the extension is for: a list of short descriptions at start, and the full text only when the task needs it. Each topic above becomes a skill, ex. `skill://inventor/drawings/SKILL.md`. |
| 4. Workflows | **Yes.** A workflow is a skill whose `SKILL.md` names the tools to call in order. |
| Knowledge for other team members | **Yes.** The skills travel in the server package from the feed, so a team member gets them with the tools, with nothing to install per client. The digest pinning means the content cannot change without a new approval. |
| The `.agents/rules/` pattern | **The same idea.** The rule frontmatter (`name`, `description`, `triggers`) is close to the Agent Skills frontmatter. The difference is the audience: `.agents/rules/` is for agents that work on this repository, and served skills are for agents that use the server. |

The extension also replaces two ideas that came up before it was found: MCP prompts for the workflows, and an
`inventor_guide(topic)` tool for the guide content. The skill format gives both of them a standard shape.

### Why it cannot be the only path yet

- **No client of this server supports it.** The
	[client matrix](https://modelcontextprotocol.io/extensions/client-matrix) lists no Skills support for
	Claude Desktop, Claude (web) or Visual Studio Code with GitHub Copilot. Claude Code and Visual Studio are not
	listed. The only support is partial: ChatGPT (a static import), fast-agent and MCP Inspector (verification).
	A community fork of Visual Studio Code has a prototype.
- **The C# SDK has no release with it.** [csharp-sdk#1864](https://github.com/modelcontextprotocol/csharp-sdk/pull/1864)
	adds a separate package, `ModelContextProtocol.Extensions.Skills`, beside `Extensions.Apps` and
	`Extensions.Tasks`. It has `WithSkillsFromDirectory(path)`, which serves every skill folder under a path, and
	`McpServerSkill.CreateFromDirectory` and `McpServerSkill.Create`, which compute the manifest from the bytes they
	serve. It also has a client API and gates by protocol version. It was open on 2026-09-24, with a competing pull
	request (#1856).
- **Approval per skill adds a step for the user.** That is correct for content from outside, but a trap such as
	"never call `CloseAll`" must apply before the user approves anything. So those traps stay in the server
	instructions.

### Recommendation

Write the content once, in the extension's format, and serve it through a fallback until clients support the
extension.

1. Put each topic and workflow in `Source/InventorMcp.Server/Skills/<name>/SKILL.md`, with Agent Skills
	frontmatter. Embed the folder in the package, as `ServerInstructions.md` is today.
2. Serve it today through one small tool (ex. `inventor_skill` with `list` and `read`). Every client supports
	tools. Add one line to `ServerInstructions.md` that names the skills and says when to read each one.
3. When #1864 (or its replacement) is released, add `WithSkillsFromDirectory` over the same folder. Keep the tool
	until the clients the team uses are on the matrix, then remove it.
4. Keep the tool changes in section 1 as the first priority. They prevent errors, and the skills only explain them.

A Claude Code plugin with the same `SKILL.md` files is a third route, but it reaches only Claude Code, and the
team also uses Claude Desktop, Visual Studio and Visual Studio Code.

## Where the results go

This document stays in `Docs/Research/` as the evidence for the plan. It is not deleted with the plan.
When the plan is done:

- Move the decisions about tools, instructions and skills to `Docs/Architecture.md`.
- Move the facts that an agent on this repository needs to `.agents/rules/inventor-interop.md` or
	`verification-status.md`.
- The guide content in section 3 then lives in the skill files themselves.
