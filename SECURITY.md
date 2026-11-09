# Security Policy

## Supported versions

Only the newest version on `master` gets security fixes. The version is in `CHANGELOG.md`.

## Report a vulnerability

Do not open a public issue for a vulnerability.

Use **Report a vulnerability** on the **Security** tab of the repository on GitHub. This opens a private report that
only the maintainers can read. Include the version, the Inventor release, the steps to reproduce the problem, and what
an attacker can do with it.

Do not put private data in a report: no client name, no file from a client project, no machine or user name, and no
log that is copied without a check. See `AGENTS.md`, "No Private Data in the Repository".

## What the server protects

The server lets an AI model run code in a live Autodesk Inventor session of the same Windows user. These are the
limits that the project keeps:

- **Only the same Windows user can connect.** The named pipe of the add-in admits only the user that runs Inventor.
	The server checks the session, the executable path and the owner of the process that hosts the pipe before it sends
	a request.
- **Each snippet is recorded.** The add-in writes each snippet to `%LOCALAPPDATA%\InventorMcp\<year>\executed-code.log`
	before it runs, with the client that sent it.
- **Unsaved work is guarded.** A snippet does not run on a document with unsaved changes, unless the call says so.
- **A user can limit the tools.** `INVENTORMCP_TOOLS=read-only` removes each tool that changes a document, and
	`INVENTORMCP_CONFIRM_RISKY_CALLS=true` makes the user confirm a call that removes a guard, answers a dialog or
	closes documents. See `README.md`, "Limit what a model can do".
- **Telemetry is off by default**, and sends only values from a fixed list. See `Docs/Telemetry.md`.

## What is not a vulnerability

- **Code that runs as the same Windows user.** A process of the same user can connect to the pipe and run code in
	Inventor. Windows does not isolate the processes of one user from each other, so the pipe is not a boundary
	against them.
- **What a snippet can do.** `inventor_eval_csharp`, `inventor_run_ilogic` and `inventor_run_plugin` run code with
	the rights of the user, by design. Use the read-only tool set when a model must not have that.
- **Instructions in document text.** The text of a rule, a property, a file name or a dialog can try to instruct a
	model. The server tells the model to treat that text as data, but a model can still make an error. Use the
	approval prompts of the MCP client, and the two settings above.
