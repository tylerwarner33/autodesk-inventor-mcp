# Tools and code execution

What each tool does, how code execution is bounded, and how to limit what a model can do.

## Tools

<details>
<summary><b>All tools</b> (the list and what each does)</summary>

| Tool | Purpose |
| --- | --- |
| `inventor_session` | Is Inventor reachable, which version, which document is active, the active project, and which open documents are modifiable. `waitSeconds` waits for an Inventor that is still starting |
| `inventor_start` | Start an Inventor release when its bridge is not running, asking which release when several can, and use it. `newInstance` starts one more process of a release that runs |
| `inventor_use_release` | Choose the Inventor release this session talks to, with no restart of the server |
| `inventor_documents` | Every open document with path, type, and unsaved state |
| `inventor_close_documents` | Close the open documents under a folder, drawings first, and never one outside it |
| `inventor_file_info` | Saved release, model states, iProperties, work points and iMates of many files, in pages |
| `inventor_test_copy` | Copy a document tree to a test folder through Apprentice, and point the copies at each other |
| `inventor_assembly_tree` | Occurrence tree with suppression, visibility, and referenced files |
| `inventor_parameters` | Parameters with kind, expression, display value, and internal value |
| `inventor_set_parameter` | Set one parameter and rebuild |
| `inventor_set_parameters` | Set many parameters with one rebuild, optionally with iLogic rules off and one rule after |
| `inventor_evaluate_expression` | Ask Inventor what an expression evaluates to, without writing it |
| `inventor_properties` | iProperties, optionally limited to one set |
| `inventor_set_property` | Set one iProperty |
| `inventor_health` | Rebuild state, sick features, error manager contents |
| `inventor_features` | Every feature with type, suppression, health and parameters, and the definition of each pattern |
| `inventor_pattern_elements` | The transform of each element of a pattern or mirror |
| `inventor_hole_check` | The holes of a part from its geometry, by diameter, with their axes |
| `inventor_styles` | The styles of a drawing or a part, with a diff against a second document |
| `inventor_update` | Rebuild a document |
| `inventor_activity` | Ordered feed of Inventor events, including every committed transaction |
| `inventor_eval_csharp` | Run a C# snippet against the live Inventor API |
| `inventor_run_ilogic` | Run an iLogic rule body, in VB.NET, or a rule of the document by name |
| `inventor_ilogic_rules` | The iLogic rules of a document, with the active flag and the length |
| `inventor_ilogic_rule_get` | Read one rule, or write every rule of a document to files |
| `inventor_ilogic_rule_set` | Change a rule at a unique anchor, with a backup and a diff |
| `inventor_api_lookup` | Search Autodesk's Inventor API documentation. Works with Inventor closed. |
| `inventor_skill` | List or read the server's guides (skills) for a topic or a workflow. Works with Inventor closed. A client that supports the Skills over MCP extension gets the same skills at `skill://inventor/<name>/SKILL.md`. |
| `inventor_orientation` | Resolve each ViewCube face to a world direction for the active document |
| `inventor_run_plugin` | Run a method from a plugin's build output, loaded fresh each call, so a code change needs no restart |
| `inventor_drawing_layout` | Measure a drawing's views, balloons, dimensions and tables, and report collisions |
| `inventor_export_sheet_image` | A drawing sheet, or a region of it, as PNG image content |
| `inventor_auto_balloon` | Replace the balloons of one assembly view: one per part, sides chosen per part or limited, in one Undo step |
| `inventor_dialogs` | The modal dialogs that block Inventor, with their text and buttons. Works while Inventor is blocked. |
| `inventor_dialog_click` | Click one visible button of a blocking dialog. Marked destructive, so a client can ask first. |

</details>

## Executing code

`inventor_eval_csharp` is the general purpose tool. It reaches every member of the Inventor API, so it covers
everything the narrower tools do not, such as creating sketches, features, and geometry.

The snippet is Roslyn script code rather than a full class.
`Application` and `Document` are in scope, `System`, `System.Collections.Generic`, `System.Linq` and `Inventor`
are imported, and `Log(...)` records a line for the caller.
`Document` is null when no document is open, and the snippet still runs, so it can create documents or run a
plugin that opens its own. `inventor_run_ilogic` still needs a document, because an iLogic rule is attached to one.

The same tool can run a plugin under development inside the live session, with no Inventor restart per change.
`inventor_run_plugin` packages that loop as one call. See `Docs/Plugin-Development-Loop.md`.

`inventor_api_lookup` answers what a member is called and what it takes, before a snippet is written.
It reads the vendored documentation file, so it needs no Inventor session.

<details>
<summary><b>Safeguards</b> (arbitrary execution inside a CAD process is bounded)</summary>

- Every snippet is appended to `%LOCALAPPDATA%\InventorMcp\<year>\executed-code.log` before it runs, with a timestamp, the
	target document, the client and the SHA-256 of the code. The code is written in full the first time, and a later
	entry of the same code has only its SHA-256, so the server's own snippets do not hide the snippets of a model.
- Execution is refused when the document has unsaved changes, unless `allowUnsavedChanges` is passed.
	Unsaved work cannot be recovered if a snippet damages it.
- Roslyn loads on first use only, so a read-only session never pays for it inside Inventor's process.
- A snippet is compiled on the thread of the bridge, not on Inventor's main thread, and the add-in keeps the 64
	compiled snippets that were used most recently. So Inventor stays responsive during a compile, and a tool that
	sends the same snippet again does not compile it again.
- Execution runs inside a `SilentOperationScope`, so a dialog cannot stall the dispatcher.
- A result of more than 40,000 characters of JSON text (10,000 for a result that failed) keeps the start and the end
	of its output and its return value. A marker line says how much was left out, and names a file with the full
	text. The result then has `outputTruncated`, `returnValueTruncated` and `fullTextFile`.

A snippet that throws is reported as a result, with its exception type and message, rather than failing the call.
Compiler errors come back as diagnostics. Each result that reports a failure has `isError` set, so a client can show
and count it.

</details>

## Limit what a model can do

Each tool tells the client what it can change: a tool that only reads has `readOnlyHint`, and a tool that writes has
`destructiveHint`. A client uses these hints to decide when to ask you. Two environment variables in the `env` of
the MCP entry add limits that a model cannot change, because the server reads them one time, at start:

| Variable | Value | Effect |
| --- | --- | --- |
| `INVENTORMCP_TOOLS` | `read-only` | The server offers only the tools that change no document, and `inventor_start` and `inventor_use_release`. There is no `inventor_eval_csharp`, `inventor_run_ilogic`, `inventor_run_plugin`, `inventor_dialog_click` and no tool that sets a value. The default is `all`. |
| `INVENTORMCP_CONFIRM_RISKY_CALLS` | `true` | The client asks you before `inventor_dialog_click`, before `inventor_close_documents`, and before each call with `allowUnsavedChanges`. If you decline, or if the client cannot show a question, the call does not run. The default is `false`. |

A value that the server does not know selects the stricter setting, and the server log has a warning.

The read-only tool set is a limit on what a model can ask for. It is not a sandbox: a read-only tool still runs the
server's own snippet in Inventor, and the server still answers the dialogs that the dialog settings permit (`Docs/Dialog-Settings.md`).
`SECURITY.md` says what the server protects, and what it does not.

## Units and parameter kinds

Inventor stores every length in centimeters and every angle in radians, whatever a document displays.
Parameter results therefore carry `internalValue` in those units alongside `expression` and `displayValue`.

Not every parameter is numeric.
Each one reports a `valueKind` of `Numeric`, `Text`, `Boolean`, or `Unknown`, because a write uses a different accessor for each.
A text parameter has no internal value, and its expression is returned with the surrounding quotation marks removed.
