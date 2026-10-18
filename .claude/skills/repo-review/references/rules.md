# Review Rules

The reason and an example for each rule of the `repo-review` skill.
Read the rule when its detection gives a hit, or when you are not sure whether a line obeys it.

## R1 Private data

`AGENTS.md`, "No Private Data in the Repository". Git history keeps a value after the file changes, and a push
publishes it. Look at code, comments, tests, docs, commit messages and the branch name.

```
// Before
string log = @"C:\Users\<name>\AppData\Local\InventorMcp\server.log";

// After
string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InventorMcp", "server.log");
```

A client name, a client file name, or a machine name has no pattern. Read each new string literal and each new doc
line for them. Text copied from a log or a dialog is the usual source.

## R2 Evidence in code comments

`AGENTS.md`, "Keep evidence out of code comments". A comment says what a change to the code needs, plus a pointer.
Dates, measurements and what was tried go in `Docs/` or `.agents/rules/`.
Code, configuration and `.agents/rules/` must not point into `Docs/Tasks/`, because a task document is deleted when
the task is done. A pointer from `Docs/` to a task is allowed.

```
// Before
// On 2026-09-12 a 40 ms wait was too short on the test machine, so we tried 200 ms and it worked.
// After
// Long enough for the add-in to accept a second pipe. See Docs/Architecture.md, "A named pipe rather than a local HTTP port".
```

A date that is a name (ex. the MCP protocol version `2026-07-28`) is not evidence.

## R3 Thin add-in

`AGENTS.md`, "Keep the add-in thin" and "Compose server-side tools before adding bridge operations".
A rebuild of the add-in needs Inventor closed, so each add-in change costs the user a restart.
A new tool can often be a canned snippet that the server sends through `inventor_eval_csharp`
(ex. `inventor_orientation`).

A change under `Source/InventorMcp.AddIn` or a new member of `BridgeOperations` is a finding unless the change says
why a snippet cannot do the job (ex. an event subscription, or work that must outlive one call).

## R4 Async

An `async void` method cannot be awaited, and an exception in it ends the process. Inside Inventor that ends
Inventor with no chance to save.
A method that returns a task has the `Async` suffix, so a reader sees that it can be awaited.
A method that waits takes a `CancellationToken`. The analyzers check that a token is forwarded (CA2016).

```
// Before
private async void OnDocumentOpened(Document document) { ... }

// After
private async Task OnDocumentOpenedAsync(Document document, CancellationToken cancellationToken) { ... }
```

Exemptions: a method with `[McpServerTool]` has the name of its tool, and an xUnit test method has the name of its
behavior.

## R5 Names and flags

- A `Manager`, `Helper` or `Utils` class has no single job, so it grows without limit. Name it for its job, or move
	each method next to the type that it works on. A `const string` that holds snippet text (ex. `_documentLengthHelper`)
	is not a class, and the rule does not apply.
- A `#region` hides code. A class that needs regions to stay readable must be split (ex. into `partial` files, as
	`InventorTool.*.cs` is).
- A `bool` parameter makes a call site that a reader cannot understand: `SetParameters(values, true)`.
	Prefer a named argument at each call site, two methods, or an enum. A finding only when a call site has a bare
	`true` or `false`.

## R6 Suppressions

Each suppression (`#pragma warning disable`, `[SuppressMessage]`, or a severity of `none`, `silent` or `suggestion`
in `.editorconfig`) needs a reason on the line above it. A suppression for one file goes in a section for that
file, not in the `[*.cs]` section.
A suppression to make a new rule pass is a finding: fix the code, or explain why the rule is wrong for this code.

## R7 Inventor session safety

The rules in `Source/InventorMcp.Server/ServerInstructions.md` also apply to the snippets that the server composes.

- Never `Documents.CloseAll`. Close only the documents that the code opened.
- A save writes the file in the format of this release, and an older release cannot open it. A save needs the user's
	request, and a check of the saved version (`inventor_file_info`).
- Inventor stores lengths in centimetres and angles in radians. A literal factor (`2.54`, `25.4`, `Math.PI / 180`) is
	a candidate: prefer `UnitsOfMeasure.ConvertUnits`. A factor for a fixed sheet size in inches is correct.
- A snippet runs on Inventor's main thread. A loop that creates sketches, views or documents must split over several
	calls, to stay under about 10 s.

## R8 Docs follow the code

- A new or changed MCP tool needs its usage in `ServerInstructions.md` or in a skill under
	`Source/InventorMcp.Server/Skills/`, and in `README.md` if a user sees it.
- A new design decision needs a section in `Docs/Architecture.md`.
- A new build, test or deploy step needs `.agents/rules/build.md`.

## R9 Tests

The test project has three levels, selected by the trait `Level`: `Unit`, `Desktop` and `Live`.
New behavior in the server needs a `Unit` test. A change to dialog handling needs a `Desktop` test when
`InventorMcp.TestDialogs` can show the dialog. A change that only a live Inventor can show goes on the list in
`Docs/Tasks/Live-Verification.md`.
