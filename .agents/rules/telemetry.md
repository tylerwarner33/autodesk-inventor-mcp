---
name: Telemetry
description: How to add a span tag, a metric tag or an instrument to the server's OpenTelemetry, and keep the two allowlists the same
triggers:
  - Editing a file in Source/InventorMcp.Server/Telemetry/
  - Adding a SetTag, a TagList, a span or an instrument anywhere in the server
  - Editing a config in Tools/Telemetry/, or the Grafana dashboard
  - Updating the ModelContextProtocol or OpenTelemetry packages
globs:
  - "Source/InventorMcp.Server/Telemetry/**"
  - "Tools/Telemetry/**"
---

## The rule

Every exported value is planned. Two guards remove the rest: the server (`AllowlistProcessor` and the metric view in
`TelemetryRegistration`) and the Collector (`transform/allowlist` in both `Tools/Telemetry/**/otel-collector.yaml`).
See `Docs/Telemetry.md`, "What is sent".

## Add a tag

1. Add the name as a constant in `TelemetryTags`, and add it to `Allowed`. `AllowedOnMetrics` follows, without the IDs
	of one session or one request.
2. Set it through the constant, never a string literal. `TelemetryTagsTests.ServerSetsNoTagWithALiteralName` fails on
	a literal.
3. Add it to the `keep_keys` lists of both Collector configs. `TelemetryTagsTests.CollectorKeepsTheSameTagsAsTheServer`
	fails when they differ.
4. Send only a fixed kind of value. A value from outside the server goes through a `TelemetryTags.*Value` method that
	sends `other` for an unknown value (ex. `ExceptionTypeValue`, `BridgeErrorValue`). Never a path, a document name,
	snippet text, a dialog text or an exception message. See `AGENTS.md`, "No Private Data in the Repository": the same
	data must not leave the machine either.
5. Add the tag to the tables in `Docs/Telemetry.md`, "What is sent".

## Add an instrument

1. Create it in `ServerTelemetry`, with a name that starts with `inventor.`, a unit (`s` for a duration, `{call}` for
	a count), and a record method. A duration histogram in `s` gets the buckets of the MCP conventions from the view.
2. Test it with `TelemetryCapture` in a unit test. The meters are static and the tests run in parallel, so find the
	measurements of a test by a value that only it uses.
3. Check the Prometheus name (a unit suffix and `_total`) in the Prometheus UI, then add a panel to
	`Tools/Telemetry/grafana/dashboards/inventor-mcp.json` and a line to `Docs/Telemetry.md`, "Read the dashboard".

## A library update

`LibraryTelemetryTests` checks the span name, the source name and the tags of the MCP library. When it fails after an
update of `ModelContextProtocol`, change `ServerTelemetry.LibrarySourceName` or the allowlist, and record what changed
in `Docs/Research/OpenTelemetry-Findings.md`.

## Check by hand

Start the stack (`Tools/Telemetry/Start-TelemetryStack.ps1`), point a server at `http://127.0.0.1:4317`, and read the
trace in Tempo. Stdout must hold only JSON-RPC lines.
