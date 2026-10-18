# Autodesk Inventor MCP Server

An MCP server that exposes a live Autodesk Inventor session, 2025 through 2027.
See `README.md` for setup, `Docs/Architecture.md` for the decisions, and `Docs/Tasks/` for outstanding work.

These are the instructions for a coding agent that works on this repository (ex. Claude Code, GitHub Copilot).
The rules a model needs to *use* the Inventor tools travel with the server instead, in
`Source/InventorMcp.Server/ServerInstructions.md`.

## Architecture

An MCP client talks stdio to `InventorMcp.Server`, which talks over a named pipe to `InventorMcp.AddIn`,
which runs inside `Inventor.exe` and calls the COM API on Inventor's main thread.
`InventorMcp.Contracts` holds the bridge messages both sides share.
`InventorMcp.AddIn.Loader` isolates the add-in on Inventor 2025 and 2026.

## Working Principles

- **Keep the add-in thin. Prefer the server.** A rebuild of the add-in needs Inventor closed.
- **Compose server-side tools before adding bridge operations.** A new tool can often be a canned snippet sent
	through `inventor_eval_csharp`, with no add-in rebuild. `inventor_orientation` is built this way.
	Add a bridge operation only when a snippet genuinely cannot do the job.
- **Look the API up rather than guessing.** `inventor_api_lookup` searches Autodesk's own documentation and works
	with Inventor closed. Use it to confirm a member exists and what it takes before writing a snippet.
- **Verify against the model, not the return value.** Check `inventor_health` after any write.
- **Keep evidence out of code comments.** A remark says what a change to the code needs, plus a pointer.
	Dates, measurements and what was tried go in `Docs/` or `.agents/rules/`, never in `Docs/Tasks/`, which is deleted.
	Evidence that a task comes from goes in `Docs/Research/`, and the task points to it.
- **Fix an analyzer error. Do not suppress it.** The build enforces the analyzers and the style rules, and each warning
	is an error. Suppress a rule only when it is wrong for the code, with the reason on the line above.
	See `Docs/Architecture.md`, "The build enforces the code rules".
- **Review in a new session.** The `repo-review` skill (`.claude/skills/repo-review/`) checks a change against the
	rules that a build cannot check. The session that wrote the code is the worst reviewer of it.

## No Private Data in the Repository

This rule applies to every person and every AI agent, and to every file, code comment, test, commit message,
branch name and pull request. Git history keeps a value after the file changes, and a push publishes it.

Do not write:

- Secrets: API keys, tokens, passwords, connection strings, certificates or license keys.
- Client data: the name of a client or a client project, and the names of its files, folders, rules, parameters,
	products or jobs. Write a neutral name (ex. `Test Drawing.idw`, `Drawing_Test`, "a client project").
- Machine and user data: machine names, user names, email addresses (other than the commit author), and absolute
	user paths. Write a variable (ex. `%LOCALAPPDATA%\InventorMcp`, `%USERPROFILE%`).
- Text copied from a log, a transcript or a dialog without a check for the data above. Logs on this machine hold
	real file paths and client names.

Before each commit, read the diff for this data. If you find it in a commit that is not pushed, change that commit.
If it is pushed, tell the owner of the repository. Do not try to remove it from the remote history yourself.

## On-Demand Rules

The rules are not loaded automatically. Before acting on a task, scan this list and open any rule whose trigger fits
the request or the files about to change. If several match, open all of them.

- **`.agents/rules/inventor-modeling.md`** - Open before writing geometry, sketches, or features in a live session, or when a request names a face (ex. "the top face"), a direction, or units. Points to the server's `modeling` skill, which has the ViewCube mapping, sketch-relative extent direction, and centimetres and radians.
- **`.agents/rules/inventor-interop.md`** - Open when editing `Source/InventorMcp.AddIn/`, or writing a snippet that touches parameters, `UnitsOfMeasure`, health, `ErrorManager`, or transactions. Covers the add-in's interop facts and the long snippet limit, and points to the server's skills (`Source/InventorMcp.Server/Skills/`) for the snippet facts.
- **`.agents/rules/addin-isolation.md`** - Open when touching `InventorMcp.AddIn.Loader`, the `.addin` manifest template, the bundle layout, add-in package references, or the Roslyn scripting host, or when debugging a load context or globals cast failure.
- **`.agents/rules/build.md`** - Open before running `dotnet build`, deploying the add-in or bundle, reading logs, or driving the server by hand. Also for an analyzer or style error in the build, a file lock during a build, the local tool feed, or a client configuration entry for the server.
- **`.agents/rules/plugin-loop.md`** - Open before running or iterating on a plugin inside a live Inventor session (ex. a Design Automation plugin), loading a build output from a snippet, validating drawings against a payload set, or building an `inventor_run_plugin` tool.
