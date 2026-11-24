# Changelog

The changes to the Autodesk Inventor MCP Server that users see.
Each pull request adds one section with a new version, so many versions can have the same date.
The version is also `<Version>` in `Directory.Build.props`, which the README version badge reads.
The newest version is first. The format is [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and [Semantic Versioning](https://semver.org/): a `Breaking` entry bumps the major version, an `Added` entry the minor version, and other entries the patch version.
See [.agents/rules/changelog.md](.agents/rules/changelog.md).

## [2.0.1] - 2026-11-24

### Changed
- The server instructions no longer list the skills. They tell a model to call `inventor_skill` with action `list` one time, before its first task, and to read the skills whose description fits each task. The description of each skill is now the only text that says when to read it, and each session loads less text at start.
- The `list` result of `inventor_skill` names the plugin skills that hold the Inventor API facts, in `pluginSkills`. The server instructions no longer name them.
- The dashboard panel "Skill reads and snippets, by session" also shows each `list` call. `Docs/Telemetry.md` has the steps to check that a model lists the skills one time in a session.

## [2.0.0] - 2026-11-23

### Breaking
- The Inventor API facts moved out of the server's skills into Claude plugins, so they also work with other Inventor MCP servers, add-ins and plugins. The server no longer serves the skills `interop`, `modeling`, `drawings`, `ilogic` and `projects-and-files`. To keep the facts, install the plugins `inventor-api`, `inventor-ilogic` and `inventor-data` from the marketplace `tylerwarner-autodesk` ([tylerwarner33/autodesk-agent-skills](https://github.com/tylerwarner33/autodesk-agent-skills)): ([PR #48](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/48))
  - `claude plugin marketplace add tylerwarner33/autodesk-agent-skills`
  - `claude plugin install inventor-api@tylerwarner-autodesk`, and the same for `inventor-ilogic` and `inventor-data`.

### Added
- The skills `snippets`, `verify-a-write`, `drawing-checks`, `ilogic-tools` and `file-tools`. They keep the steps with this server's tools from the moved skills: the snippet host and its helpers, the checks after a write, the drawing tools, the iLogic tools and dialogs, and the file tools. ([PR #48](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/48))

### Changed
- The server instructions and the description of `inventor_eval_csharp` tell a model to read `snippets` before its first snippet, and `inventor-api-traps` when the client has the plugin. ([PR #48](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/48))

## [1.1.2] - 2026-11-23

### Changed
- Update the workflow action `actions/create-github-app-token` from 3.3.0 to 3.4.0. ([PR #46](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/46))

## [1.1.1] - 2026-11-23

### Changed
- Update the NuGet package `Meziantou.Analyzer` from 3.0.341 to 3.0.342. ([PR #47](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/47))
- Update the NuGet package `Microsoft.Extensions.TimeProvider.Testing` from 10.11.0 to 10.11.1. ([PR #47](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/47))
- Update the NuGet package `SonarAnalyzer.CSharp` from 10.36.0.15337 to 10.37.0.15768. ([PR #47](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/47))

## [1.1.0] - 2026-11-20

### Added
- The dialog type `vaultDownloadProgress` (the Vault "Download progress", ex. after a project switch) with the new value `"Wait"`, the default. A call waits until the dialog closes by itself, for up to 10 minutes, in place of `blocked-by-dialog`. The server never clicks its `Cancel`, which stops the download. The result lists the dialog in `blockingDialogs`. ([PR #45](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/45))

## [1.0.1] - 2026-11-19

### Changed
- The README and the docs give the version of a development build as `<version>-dev.<UTC time>`, where the version is the one in `Directory.Build.props` (ex. `1.0.0-dev.20261119181710`). Before, they gave `0.1.0-dev.<UTC time>`. ([PR #44](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/44))

## [1.0.0] - 2026-11-19

### Breaking
- The add-in and the server use bridge protocol version 3. Close Inventor, build and deploy the add-in again for each release (see the README), start Inventor, and restart the MCP client. A new add-in with an old server, or an old add-in with a new server, does not connect: the old server reports `inventor-not-running`, and the new server reports `bridge-outdated`.
- The pipe name is now `InventorMcp.Bridge.v3.<year>.<process id>`, one for each Inventor process, in place of one for each release.
- The server and the add-in now speak MCP on the pipe (version `2026-07-28`, pinned on both sides), through the C# MCP SDK, in place of a line protocol of this repository. An add-in built before this release does not complete the handshake, and the server reports `bridge-outdated`: deploy the add-in again for each release.
- With no `documentName`, a tool now uses the document in edit. During an edit in place of a part in an assembly, that is the part, not the assembly.

### Added
- A `documentName` can be a full document name (the path with the model state, ex. `C:\Parts\Bracket.ipt<State2>`), to name one model state of a file. A name that matches more than one open document (ex. a path whose file has two model states open, or two documents with one display name) returns the new code `ambiguous-document`, with the full document names to use.
- `inventor_session` gives the full document name and the model state of each document, and the document in edit during an edit in place (`activeEditDocument`).
- `inventor_activity` answers while a long call runs, because it no longer waits behind a call to Inventor's main thread.
- A cancelled tool call now reaches the add-in, and a call that still waits for Inventor then does not run. A call that already runs ends normally.
- A second Inventor process of the same release now has its own bridge. When two run, a tool returns `instance-required` with the id, the start time and the window title of each process, and `inventor_use_release` takes a `processId` to choose one. `inventor_session` lists the processes with the same description. A session keeps its process while it runs. The retry of a call after a dropped connection never goes to another process. After a restart of Inventor, the next call uses the new process when it is the only one.
- `version-mismatch` tells the user to restart the MCP client when an add-in is newer than the server.

### Changed
- The bridge pipe refuses a client from another computer (a deny entry for the `NETWORK` SID), and only the first instance can claim the name.
- After a connect, the server compares the process id in the pipe name with the process that hosts the pipe. A different id refuses the pipe with `untrusted-bridge-host`.
- `bridge-outdated` now also covers a pipe of protocol version 2.
- Each line of `addin.log` has the id of its Inventor process, because two processes of one release write to the same file. An append that finds the file open in another process is tried again, so a line is not lost.
- After an unload and a load of the add-in in one Inventor, the bridge tries to claim its pipe name 5 times before it stops.
- The `untrusted-bridge-host` message for a refused account also names a server that runs with a network logon (ex. through SSH or WinRM).
- Every tool finds its document with the same rules, in the add-in. Before, a name matched the first document with that path or display name, so a tool could use the wrong model state of a file, or the wrong one of two documents with one display name, with no message.
- The add-in runs the calls of one server one at a time, so a cancelled call and the next call never run at the same time.
- `inventor_orientation` says that it reads the camera of the active view, which belongs to the top document, also during an edit in place.

### Removed
- The add-in operation `parameters.set`. `inventor_set_parameter` uses the same snippet as `inventor_set_parameters` since version 0.5.0.

## [0.5.1] - 2026-11-19

### Changed
- Update the NuGet package `Serilog.Extensions.Logging` from 10.0.0 to 11.0.0. ([PR #43](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/43))

## [0.5.0] - 2026-11-19

### Added
- `InTransaction(document, name, () => { ... })` for `inventor_eval_csharp` snippets. It runs the action as one Undo step on that document, and aborts the step if the action throws.

### Changed
- `inventor_set_parameters` now makes one Undo step in Inventor, named `MCP: Set Parameters`. Before, each parameter made its own step. A failure outside the loop over the parameters (ex. in the rebuild) reverts the document. A batch with failed entries keeps the parameters that were written.
- `inventor_set_parameter` now uses the same path. A numeric expression that Inventor cannot parse gives `invalid-expression`, and an unknown name gives `not-found`. The result has `valueKind`, `expression`, `internalValue`, `displayValue`, `units` and `transaction`, and no longer has `parameterType`, `isDriven`, `isKey` and `comment`: read them with `inventor_parameters`.
- When Inventor writes a parameter but cannot read it back, the entry is still a success, with `readBackError`. A repeat of the write is not necessary.
- `runRuleAfter` of `inventor_set_parameters` now runs inside the Undo step, so one Undo reverts what the rule changed too.
- The `interop` skill says that a user parameter cannot have the name of a unit (ex. `A` or `T`).

## [0.4.13] - 2026-11-19

### Changed
- A result of `inventor_eval_csharp` over 40,000 characters (10,000 for a failed result) now keeps its start and its end, with a `[TRUNCATED: ...]` marker line between them. Before, it kept only the start and cut at 60,000 characters. The marker names a file in `%LOCALAPPDATA%\InventorMcp\Results` with the full text, because the audit log does not keep the output. The server deletes these files after 14 days.
- The limit now covers the whole JSON text of the result, in place of each of the output lines and the return value. It counts the characters after escaping, and the diagnostics and the exception message are in it. A line longer than 30 % of the limit is cut to it, so the last line keeps its end.
- A result that was cut has `outputTruncated`, `returnValueTruncated` and `fullTextFile`.
- Tool results now write a quote in a string as `\"` and keep characters that are not ASCII, in place of `"` and `\uXXXX`. A result is shorter, and its JSON is the same.
- `inventor_assembly_tree` now uses the same limit of 40,000 characters, in place of 60,000.
- A result with more than 50 diagnostics keeps the first 50 and gives the number that was left out.

## [0.4.12] - 2026-11-19

### Changed
- A call that only reports that Inventor cannot be reached is no longer marked as an error: `inventor_session` and `inventor_dialogs` with `inventor-not-running`, and `inventor_start` with `inventor-starting-or-no-bridge` or `version-required`. The result tells the model what to do next, and is not a failure of the call.
- `inventor_set_parameters` now gives the counts `requested`, `succeeded` and `failed`. When no parameter was set, the result has the error `no-parameter-set`. A batch with some successes is not an error.
- The line of each tool call in the server log now says if the result was an error (`Result ok`, `error` or `exception`).

## [0.4.11] - 2026-11-19

### Changed
- Each tool that writes now says if a second call with the same arguments gives the same result (`idempotentHint`). Closing documents, setting an iProperty, rebuilding a document and reading a rule are idempotent. Parameter writes, rule writes, auto balloon, dialog clicks, test copies and the code execution tools are not.
- `inventor_update` is no longer marked as destructive (`destructiveHint: false`). A rebuild changes no value that the user set.

## [0.4.10] - 2026-11-19

### Changed
- A connection that fails now gives the cause. A bridge with every connection in use gives the new code `bridge-connections-in-use`, and the message tells the model to ask the user to close an MCP client, not to wait and not to start Inventor.
- When Inventor runs but its add-in has no bridge, the message now says if the add-in is not deployed, is probably still loading (Inventor started less than 120 s ago), or is not loaded. Each message keeps the code `inventor-not-running` and tells the model not to start a second Inventor.
- A pipe that refuses the Windows account now gives `untrusted-bridge-host`, not an unreadable error.
- `inventor_start` now returns a readable result when the connection fails during its wait.
- The server log now says if the process of a dropped connection still runs.

## [0.4.9] - 2026-11-19

### Changed
- Update the NuGet package `Microsoft.Extensions.Hosting` from 10.0.14 to 10.0.15. ([PR #35](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/35))

## [0.4.8] - 2026-11-19

### Changed
- Dependabot no longer opens pull requests for the .NET SDK version in `global.json`. A patch update changes nothing, because `rollForward` `latestFeature` already uses the newest .NET 10 SDK. Change `global.json` by hand when the target framework moves.

## [0.4.7] - 2026-11-16

### Changed
- Dependabot no longer proposes a new major version of the .NET SDK or of the `Microsoft.Extensions.*` packages, because the projects target .NET 10.

## [0.4.6] - 2026-11-16

### Changed
- Update the NuGet package `Meziantou.Analyzer` from 3.0.322 to 3.0.341. ([PR #30](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/30))
- Update the NuGet package `OpenTelemetry.Exporter.OpenTelemetryProtocol` from 1.20.1 to 1.21.0. ([PR #30](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/30))
- Update the NuGet package `OpenTelemetry.Extensions.Hosting` from 1.20.1 to 1.21.0. ([PR #30](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/30))
- Update the NuGet package `SonarAnalyzer.CSharp` from 10.35.0.4138 to 10.36.0.15337. ([PR #30](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/30))

## [0.4.5] - 2026-11-16

### Changed
- The build uses the .NET 10 SDK that `global.json` gives, not the newest SDK on the machine. Dependabot updates the SDK version. ([PR #31](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/31))

### Fixed
- The tools that parse the JSON result of a snippet (ex. `inventor_styles`) release the buffer of the parser. ([PR #31](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/31))
- The build no longer fails with CA2026 on a machine that has the .NET 11 SDK. ([PR #31](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/31))

## [0.4.4] - 2026-11-10

### Changed
- Update the NuGet package `Meziantou.Analyzer` from 3.0.307 to 3.0.322. ([PR #26](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/26))
- Update the NuGet package `Microsoft.Extensions.Hosting` from 10.0.9 to 10.0.14. ([PR #26](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/26))
- Update the NuGet package `Microsoft.Extensions.TimeProvider.Testing` from 10.10.0 to 10.11.0. ([PR #26](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/26))
- Update the NuGet package `xunit.v3` from 4.0.2 to 4.0.3. ([PR #26](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/26))

## [0.4.3] - 2026-11-09

### Fixed
- The changelog entry of a grouped Dependabot update gives the old version of each package, not only the new one. ([PR #28](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/28))

## [0.4.2] - 2026-11-09

### Changed
- Update the workflow action `actions/checkout` from 5.1.0 to 7.0.1. ([PR #25](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/25))
- Update the workflow action `actions/setup-dotnet` from 5.4.0 to 6.1.0. ([PR #25](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/25))

## [0.4.1] - 2026-11-09

### Changed
- Each Dependabot pull request gets its version in `CHANGELOG.md` and `Directory.Build.props` from a workflow, with one entry for each updated package. See `.agents/rules/changelog.md`, "Dependabot pull requests". ([PR #27](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/27))

## [0.4.0] - 2026-11-09

### Added
- `INVENTORMCP_TOOLS=read-only` in the `env` of a client entry makes the server offer only the tools that change no document. See the README, "Limit what a model can do". ([PR #24](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/24))
- `INVENTORMCP_CONFIRM_RISKY_CALLS=true` makes the client ask the user before `inventor_dialog_click`, before `inventor_close_documents`, and before each call with `allowUnsavedChanges`. The call does not run if the user declines or if the client cannot show a question. ([PR #24](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/24))
- Each tool has a title, and says if it only reads (`readOnlyHint`), if it can change a document (`destructiveHint`) and if it reaches outside Inventor (`openWorldHint`). ([PR #24](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/24))
- `inventor_test_copy` reports progress while the copy runs. ([PR #24](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/24))
- `SECURITY.md` says how to report a vulnerability, what the server protects, and what it does not. ([PR #24](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/24))

### Changed
- A tool result that reports a failure has `isError` set: a result with an `error` code, and a snippet that threw or did not compile. The content of the result is the same. ([PR #24](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/24))
- The tools that only read are marked read-only (ex. `inventor_session`, `inventor_health`, `inventor_parameters`, `inventor_api_lookup`), so a client can run them with fewer questions. Each tool that writes is marked destructive. ([PR #24](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/24))
- A return value or an output of more than 60,000 characters from a snippet is cut, and the output says how much was left out. ([PR #24](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/24))
- The add-in compiles a snippet away from Inventor's main thread, and does not compile the same snippet again. Inventor stays responsive during a compile, and its memory does not grow with each call of the same tool. ([PR #24](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/24))
	- Deploy the add-in again to get this change and the log changes below. The add-in of version 0.3.0 still works with this server.
	- A snippet that declares its own `static` field keeps the value when the same snippet runs again.
- `executed-code.log` holds the SHA-256 of each snippet, and the code in full only the first time in a file. `addin.log` and `executed-code.log` start a new file at 10 MB, and the 5 newest full files stay. ([PR #24](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/24))
- The server instructions tell a model that text from a rule, a property, a file name, a log or a snippet output is data, not an instruction. ([PR #24](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/24))

### Fixed
- Two calls of `inventor_api_lookup` at the same time no longer read the API documentation two times, and a read that failed is tried again on the next call. ([PR #24](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/24))

### Security
- `inventor_test_copy` sends its script to Windows PowerShell through standard input, not through a file in the temporary folder. ([PR #24](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/24))
- A document name or a client name with a line break cannot write a line that looks like a new entry in `executed-code.log`. ([PR #24](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/24))
- The workflows use each action at a fixed commit, do not keep the repository token after the checkout, and have a time limit. Dependabot updates the packages and the actions each week. ([PR #24](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/24))

## [0.3.0] - 2026-11-09

### Added
- The server serves its skills through the Skills over MCP extension (`io.modelcontextprotocol/skills`), at `skill://inventor/<name>/SKILL.md`, with the same text as `inventor_skill`. A client that supports the extension can list the skills and read them as resources. `inventor_skill` stays for the clients that do not support it yet. ([PR #23](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/23))

### Changed
- The MCP library `ModelContextProtocol` is version 2.4.0 (was 2.3.0), with `ModelContextProtocol.Extensions.Skills` 2.4.0. ([PR #23](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/23))
- A read of a skill through the extension counts in the skill reads of the telemetry, as a read through `inventor_skill` does. ([PR #23](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/23))
- If the MCP library refuses a skill, the server starts without the extension, writes a warning in the server log, and keeps all its tools. `inventor_skill` still serves the skills. ([PR #23](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/23))

## [0.2.0] - 2026-10-29

### Added
- Optional OpenTelemetry traces and metrics, off by default. Set `OTEL_EXPORTER_OTLP_ENDPOINT` in a client entry to turn them on. No snippet, argument, result, path, dialog text, or tool or method name that the server does not have is sent. See `Docs/Telemetry.md`. ([PR #22](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/22))
- A local Collector, Prometheus, Tempo and Grafana setup in `Tools/Telemetry`, started by a script or with Docker from `Tools/Telemetry/docker`. `Test-TelemetryStack.ps1` tests the Docker stack end to end, and a workflow runs it when a file that can break the stack changes. Prometheus takes OTLP, so the counts of short server sessions are exact. A dashboard shows the time and failures of each tool, the main thread time of `inventor_eval_csharp`, skill reads, connections to each release, dialogs, and the sick features that `inventor_health` finds. ([PR #22](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/22))
- `inventor_start` returns `waiting-for-user`, and `inventor_session` with `waitSeconds` stops early with `blocked-by-dialog`, when a dialog holds Inventor before the bridge add-in loads (ex. the Vault "Log In"). `inventor_dialogs` also reads that dialog. ([PR #22](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/22))
- The dialog type `vaultLogIn`, which the server never answers. ([PR #22](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/22))

### Changed
- The MCP library `ModelContextProtocol` is version 2.3.0 (was 2.0.0). ([PR #22](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/22))
- With telemetry on, each line of the server log ends with the trace ID of its tool call. ([PR #22](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/22))
- A snippet result no longer holds the compiler warnings CS1701 and CS1702, which Inventor 2026 gave in each result. ([PR #22](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/22))
- `Docs/Telemetry.md` and the README say who telemetry is for, that the server only sends data, what each `OTEL_*` variable does, and that the logs do not change. The README has a table of contents and a quick start, follows the order of use (get started, use it, diagnose it, contribute), and collapses the sections for each client, the add-in internals, the tool list and the dialog rules. ([PR #22](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/22))
- The skills name two traps of `PartFeature.Delete` and of feature names, and say to create the first document of a session in its own call. ([PR #22](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/22))

### Fixed
- After Inventor closed, `inventor_start` no longer reports the new Inventor as started at once through the pipe of the closed one, and `inventor_dialogs` no longer examines the closed process. ([PR #22](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/22))

## [0.1.0] - 2026-10-28

### Added
- The changelog and the versioning rule. Each pull request adds the next version to this file and to `Directory.Build.props`, and a workflow checks it.
- The first versioned state of the server. It has the tools to inspect and drive a live Inventor 2025, 2026 or 2027 session, as listed in the README.
