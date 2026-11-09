# Changelog

The changes to the Autodesk Inventor MCP Server that users see.
Each pull request adds one section with a new version, so many versions can have the same date.
The version is also `<Version>` in `Directory.Build.props`, which the README version badge reads.
The newest version is first. The format is [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and [Semantic Versioning](https://semver.org/): a `Breaking` entry bumps the major version, an `Added` entry the minor version, and other entries the patch version.
See [.agents/rules/changelog.md](.agents/rules/changelog.md).

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
