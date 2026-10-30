# Changelog

The changes to the Autodesk Inventor MCP Server that users see.
Each pull request adds one section with a new version, so many versions can have the same date.
The version is also `<Version>` in `Directory.Build.props`, which the README version badge reads.
The newest version is first. The format is [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and [Semantic Versioning](https://semver.org/): a `Breaking` entry bumps the major version, an `Added` entry the minor version, and other entries the patch version.
See [.agents/rules/changelog.md](.agents/rules/changelog.md).

## [0.2.0] - 2026-10-29

### Added
- Optional OpenTelemetry traces and metrics, off by default. Set `OTEL_EXPORTER_OTLP_ENDPOINT` in a client entry to turn them on. No snippet, argument, result, path, dialog text, or tool or method name that the server does not have is sent. See `Docs/Telemetry.md`. ([PR #22](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/22))
- A local Collector, Prometheus, Tempo and Grafana setup in `Tools/Telemetry`. Prometheus takes OTLP, so the counts of short server sessions are exact. A dashboard shows the time and failures of each tool, the main thread time of `inventor_eval_csharp`, skill reads, connections to each release, dialogs, and the sick features that `inventor_health` finds. ([PR #22](https://github.com/tylerwarner33/autodesk-inventor-mcp/pull/22))
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
