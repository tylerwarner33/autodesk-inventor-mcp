# OpenTelemetry Findings

Facts found while planning and building OpenTelemetry for the server. The decisions are in `Docs/Architecture.md`,
"Telemetry is optional, and goes through a Collector", and the runbook is `Docs/Telemetry.md`.
The section "Phase 0 results" holds the checks made before and during the build.

## The MCP library has its own telemetry

Found on 2026-10-29 by a string scan of `ModelContextProtocol.Core.dll` 2.0.0 (`lib/net10.0`, from the NuGet cache),
in UTF-16 and UTF-8. The scan shows that the names exist in the assembly. It does not show when the library uses them.

| Kind | Strings found |
| --- | --- |
| Source or meter name | `Experimental.ModelContextProtocol` |
| Types used | `ActivitySource`, `Meter`, `CreateHistogram` |
| Metric names | `mcp.client.operation.duration`, `mcp.client.session.duration`, `mcp.server.session.duration`, and the part `operation.duration` (so `mcp.server.operation.duration` is likely built from parts) |
| Tag names | `gen_ai.operation.name`, `gen_ai.tool.name`, `gen_ai.prompt.name`, `mcp.method.name`, `mcp.protocol.version`, `mcp.resource.uri`, `mcp.session.id`, `rpc.request.id`, `rpc.response.status_code`, `error.type`, `network.transport` |
| Not found | `traceparent`, `tracestate`, `baggage` |

## What the library source says

Read on 2026-10-29 from `src/ModelContextProtocol.Core/Diagnostics.cs` on the `main` branch of
`modelcontextprotocol/csharp-sdk`. The `main` branch can differ from 2.0.0, so Phase 0 of the plan confirms each point
against the version in `Directory.Packages.props`.

- The `ActivitySource` and the `Meter` are both named `Experimental.ModelContextProtocol`.
- Each duration histogram has the unit `s` and the bucket boundaries of the MCP semantic conventions: 0.01, 0.02,
	0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 30, 60, 120, 300 s.
- The library instruments a request only when the source has a listener (`ActivitySource.HasListeners()`). With no
	OpenTelemetry SDK registered, it does no tracing work.
- Trace context goes in `params._meta`, through `DistributedContextPropagator`, so the keys are those of the
	configured propagator (W3C `traceparent` and `tracestate` by default). The server extracts them from each request
	and notification, and injects them into what it sends. That is why the string scan found no `traceparent`.
- The library reuses an outer span whose name starts with `execute_tool `, so it does not nest a second span for the
	same tool execution.
- The file has no switch for sensitive data.

## Trace context in `_meta` is a standard

SEP-414 (`https://modelcontextprotocol.io/seps/414-request-meta`) is Final. When trace context goes in `_meta`, the
keys `traceparent`, `tracestate` and `baggage` follow W3C Trace Context and W3C Baggage, with no DNS prefix. It
names the C# SDK as a reference implementation. It does not require a client to send them.

Discussion 269 (`https://github.com/modelcontextprotocol/modelcontextprotocol/discussions/269`) proposed that a server
send its spans back to the client in a notification. It closed with no consensus. The OpenTelemetry contributors in
it preferred that a server export OTLP directly, and that propagation comes first. This plan does that.

## The 2026-07-28 specification

Read on 2026-10-29 from `https://modelcontextprotocol.io/specification/latest/changelog`.

- Minor change 2 documents the trace context keys `traceparent`, `tracestate` and `baggage` in `_meta` (SEP-414). So
	trace context is now part of the specification, not only a convention of some SDKs.
- SEP-2577 deprecates the Logging feature, with Roots and Sampling. The suggested migration for Logging is: log to
	`stderr` (stdio) or use OpenTelemetry. This server already logs to a file and not through MCP Logging, so nothing
	changes. It supports the direction of this plan.
- The specification does not define spans, metrics or tag names. Those are in the OpenTelemetry semantic conventions.

## The OpenTelemetry semantic conventions for MCP

Read on 2026-10-29 from `docs/gen-ai/mcp.md` in `open-telemetry/semantic-conventions-genai` (the MCP page moved there
from `open-telemetry/semantic-conventions`).

- Status: **Development**, so names can change. `error.type` and `network.transport` are Stable.
- Span name: `{mcp.method.name} {target}`, ex. `tools/call inventor_eval_csharp`. The server span has the kind
	`SERVER`. When an outer `execute_tool` span already exists, MCP instrumentation adds its attributes to it and does
	not start a new span. The C# SDK does this (see "What the library source says").
- `network.transport` is `pipe` for stdio.
- `error.type`: the JSON-RPC error code as a string. A `CallToolResult` with `isError` true gives `tool_error`. On a
	success it is not set. On the server, `-32700`, `-32600`, `-32601`, `-32602` and `-32002` are not errors.
- **Opt-in, sensitive:** `gen_ai.tool.call.arguments`, `gen_ai.tool.call.result` and `gen_ai.prompt.variable.<name>`.
	They are off unless an implementation turns them on.
- Metrics, all histograms in `s` with the buckets 0.01 to 300 s: `mcp.server.operation.duration`,
	`mcp.server.session.duration`, and the two client metrics. `mcp.server.operation.duration` carries
	`mcp.method.name`, `gen_ai.tool.name`, `error.type`, `mcp.protocol.version`, `network.transport` and others.
- The request ID attribute is `jsonrpc.request.id` in the current conventions. The 2.0.0 assembly has
	`rpc.request.id`, so the names in the SDK lag behind the conventions.

What this means: this server returns a failure as a normal result (`SafeAsync`), not with `isError`. So the library
records a failed bridge call as a success, with no `error.type`. The plan's own `inventor.outcome` and `error.type`
tags close that gap.

## The C# SDK releases

Read on 2026-10-29 from `https://github.com/modelcontextprotocol/csharp-sdk/releases`.

- The newest release is 2.3.0 (20 October). The repository uses 2.0.0 (28 July).
- 2.3.0: "Correct protocol-version telemetry for stateless server spans" (#1810).
- 2.1.0: "Add Application Insights telemetry example" (#1778), and more guidance for telemetry.
- The SDK documentation (`docs/concepts`) has no page for telemetry. The guidance is in the sample
	`samples/AspNetCoreMcpServer/Program.cs`: `AddOpenTelemetry()`, `WithTracing(b => b.AddSource("*"))`,
	`WithMetrics(b => b.AddMeter("*"))`, `WithLogging()`, then `UseOtlpExporter()`, or `UseAzureMonitor()` when an
	Application Insights connection string is set.

What this means: the sample listens to every source (`"*"`). This plan names each source instead, so that a source
from another package cannot export a value that the allowlist did not plan for.

## Claude Code and MCP servers

Read on 2026-10-29 from `https://code.claude.com/docs/en/monitoring-usage`.

- Claude Code's traces are a beta. They need `CLAUDE_CODE_ENABLE_TELEMETRY=1`,
	`CLAUDE_CODE_ENHANCED_TELEMETRY_BETA=1` and `OTEL_TRACES_EXPORTER=otlp`. A tool call is the span
	`claude_code.tool`. For an MCP tool, `tool_name_safe` is `mcp_other`. The server and tool names are recorded only
	with `OTEL_LOG_TOOL_DETAILS=1`.
- The documentation says that **outbound HTTP MCP requests** carry `traceparent`, and that Bash and PowerShell
	subprocesses get a `TRACEPARENT` variable. It says nothing about `_meta` on a **stdio** MCP server. A feature
	request to propagate W3C trace context into MCP tool requests exists in the Claude Code issue tracker.
- "Claude Code doesn't pass `OTEL_*` environment variables to the subprocesses it spawns, including the Bash tool,
	hooks, MCP servers, and language servers." The documentation does not say whether this also removes an `OTEL_*`
	value set in the `env` block of an MCP server entry.

What this means:

- The plan's switch is `OTEL_EXPORTER_OTLP_ENDPOINT` in the MCP entry's `env`. If Claude Code removes it there too,
	telemetry can never turn on from Claude Code. Phase 0 must test this first.
- Do not expect one trace from Claude Code into this server over stdio. Claude Code's own traces can go to the same
	Collector, and the two can be compared by time and tool name.

## What this means for the plan

- The library starts a span and records a duration for each tool call when a listener exists. The server must not
	start a second span for the same call. It adds its own tags to the library's span.
- If a client sends `traceparent` in `_meta`, the server's spans join the client's trace with no extra code. Whether
	a client in use (ex. Claude Code) sends it is still open.
- The names start with `Experimental.`, so they can change in a later library version.
- The library's tags can include values that the server does not control (ex. `mcp.resource.uri`). The server's tag
	allowlist must cover the library's spans and metrics too.

## Phase 0 results

Checked on 2026-10-29, with `ModelContextProtocol` 2.3.0, `OpenTelemetry` 1.20.1, Claude Code 2.1.312, the Collector
contrib build 0.164.0, Prometheus 3.15.1, Tempo 3.1.1, Grafana 13.3.0 and the Aspire dashboard 13.6.4.

### Claude Code passes `OTEL_*` from an entry's own `env`

A headless `claude -p` run with a temporary MCP entry started a script that wrote only "present" or "missing" for
each name. `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_SDK_DISABLED` and a control variable were all present. So the rule
"Claude Code doesn't pass `OTEL_*` environment variables to the subprocesses it spawns" covers what Claude Code
inherits, not the `env` of an entry. The standard variable is the switch, and the server has no fallback variable.

### The MCP library upgrade

The server moved from 2.0.0 to 2.3.0 in the same branch, with no code change and a clean build. The checks below are
on 2.3.0.

### The library's spans and metrics

From `LibraryTelemetryTests`, which runs a real server and client in memory:

- Each request is a span of the source `Experimental.ModelContextProtocol`. A tool call is `tools/call <tool>`, kind
	`Server`, with `mcp.session.id`, `jsonrpc.request.id`, `mcp.method.name`, `network.transport` (`pipe`),
	`mcp.protocol.version`, `gen_ai.tool.name` and `gen_ai.operation.name` (`execute_tool`). The request ID is
	`jsonrpc.request.id` in 2.3.0, as in the current conventions (2.0.0 had `rpc.request.id`).
- `Activity.Current` in the call tool filter is that span. So the server adds its tags to it, and starts no second
	span.
- The client side injects trace context into `_meta`, and the server span is its child. Propagation works over a
	stream transport.
- Metrics: `mcp.server.operation.duration` (unit `s`, with `gen_ai.tool.name`, and `error.type` on a failure) and
	`mcp.server.session.duration`.

### A thrown tool exception

- The library adds no exception event.
- The span status is `Error`, and **the status description holds the content of the `isError` result** (for a throw,
	"An error occurred invoking '<tool>'."). A tool that returns its own `isError` text would put that text there. So
	the server's allowlist processor clears the status description, and the Collector sets `span.status.message` to an
	empty string.
- The exception passes through the call tool filter, and the library makes the `isError` result after it. So the
	server records the exception type, and its `error.type` (ex. `System.InvalidOperationException`) replaces the
	library's `tool_error` on the span.

### Exit and start time

Measured with the four requests of `Docs/Architecture.md` (`initialize`, `tools/list`, `inventor_api_lookup`, and
`inventor_skill` in place of `inventor_session`), through `dotnet tool exec` from the local feed, with the version
cached. Time to the fourth answer, and time from the close of stdin to the end of the process:

| Telemetry | Fourth answer | Exit |
| --- | --- | --- |
| Off | 1.0 s | 0.03 s |
| On, nothing listens on the port, SDK default export timeout (10 s) | 1.1 s | 6.2 s |
| On, an address that does not answer, SDK default | 1.1 s | 15.1 s |
| On, either case, export timeout 1 s | 1.1 s | 3.1 s |
| On, either case, export timeout 500 ms | 1.1 s | 1.6 s |

In each case no tool call failed or slowed, stdout held only JSON-RPC lines, and stderr was empty. The last exports
run at the end, and each waits for the timeout. So the server sets `OTEL_EXPORTER_OTLP_TIMEOUT` to 500 ms when the
user sets none. A local Collector answers in a few milliseconds.

### The protocol and the address

- The .NET OTLP exporter uses gRPC by default. The Collector and the Aspire dashboard accept gRPC and HTTP. The runbook
	uses gRPC on 4317.
- **`http://localhost:4317` sent nothing**, and `http://127.0.0.1:4317` worked, with the Collector on `127.0.0.1`
	only. Use `127.0.0.1` in the endpoint.

### Exemplars

The SDK records no exemplar by default. With `SetExemplarFilter(ExemplarFilterType.TraceBased)`, Prometheus stored
exemplars with the labels `trace_id` and `span_id` only.

### The trace ID in Serilog

A Serilog event written inside a span has the `TraceId` and `SpanId` of `Activity.Current`
(`LibraryTelemetryTests.SerilogTakesTheTraceIdOfTheSpan`). With telemetry on, the file log writes `{TraceId}` at the
end of each line, and Tempo finds the trace by that ID.

### Logs are not exported

`BridgeClient.LogBlockingDialog` writes the title and the text of a dialog, which can name a client file, and the
start writes the path of the user's dialog settings. So the server exports no log. The trace ID in the file log links
a line to its trace.

### Claude Code sends no `traceparent` to a stdio server

With `CLAUDE_CODE_ENABLE_TELEMETRY=1`, `CLAUDE_CODE_ENHANCED_TELEMETRY_BETA=1` and `OTEL_TRACES_EXPORTER=otlp`, one
`inventor_skill` call from a headless Claude Code run made a `tools/call inventor_skill` span with no parent. So each
call from Claude Code starts its own trace.

### Field values

- `ExecutionResult.ExceptionType` is a `FullName`. A snippet or a plugin can declare its own exception class, and its
	name can hold client data. So `error.type` keeps a name only in `System.`, `Microsoft.`, `Inventor.` or
	`Autodesk.`, with no generic argument, and sends `other` for any other name. The add-in's `CompilationError` and
	`CompilationErrorException` become `compilation-error`.
- `DialogReport.Action` is free text in one case (`left open: <click message>`). So `inventor.dialog.action` comes
	from the click status, as one of `clicked`, `closed-elsewhere`, `click-failed` and `needs-person`.
- `DialogReport.Type` comes only from the constants of `DialogPolicy`, or null (sent as `none`).
- A bridge error code comes from the add-in, so the server sends it only when it is a constant of `BridgeErrorCodes`.

### The programs on Windows

Each has a Windows amd64 build: the Collector contrib (`tar.gz`), Prometheus (`zip`), Tempo (`tar.gz`, so Jaeger is not
needed), Grafana (`zip`, about 500 MB) and the Aspire dashboard (the NuGet package `Aspire.Dashboard.Sdk.win-x64`, with
`tools/Aspire.Dashboard.exe`). Each published checksum matched.

- Tempo 3 runs in monolithic mode (`target: all`) with no Kafka. Retention is
	`backend_worker.compaction.block_retention`, and the live store has its own WAL path.
- The Aspire dashboard reads `ASPNETCORE_URLS`, `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL`,
	`ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL` and `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS`. Its defaults are 18888
	(UI), 18889 (OTLP gRPC) and 18890 (OTLP HTTP). Version 13.6 also warns that its API is unsecured.
- The newest container tag of the dashboard was 13.6.2, behind the NuGet package.
- The Collector's own metrics use port 8888, which another program held on the test machine. The config turns them off.

### The Collector allowlist

- `otelcol-contrib validate` parses each OTTL statement: a statement with an unknown function fails it.
- 0.164.0 warns that the exporter name `otlp` is deprecated (use `otlp_grpc`), and that a path needs its context
	prefix (ex. `span.attributes`). The config uses both.
- `keep_keys(span.attributes, [...])`, `set(span.status.message, "")` and `keep_keys(spanevent.attributes, [])`, each
	context in its own group. A span sent straight to the Collector with `host.name`, `process.command_line`,
	`file.path`, `mcp.resource.uri`, a status message and an `exception` event with `exception.message` reached Tempo
	with only `service.name`, `gen_ai.tool.name`, the status code and the event name.

### The local stack, end to end

With the five programs started by `Tools/Telemetry/Start-TelemetryStack.ps1` and calls from the real server
(`inventor_skill`, `inventor_session`, and `inventor_eval_csharp` with `return 1;` and with a throw):

- Prometheus had each `inventor_*` metric with the planned labels, ex.
	`inventor_tool_calls_total{inventor_outcome="code-failed", error_type="System.InvalidOperationException"}`. A
	histogram bucket is `le="10.0"`.
- Tempo had the library's `tools/call` span with the server's tags, and a `bridge <operation>` span under it for each
	round trip (`inventor_session` makes two).
- Each dashboard query returned data, and each TraceQL panel found traces.
- After Prometheus, Tempo and Grafana restarted, the older trace, the counts and the dashboard were still there.
- Docker Compose (Step C) was not run: the test machine has no Docker or Podman. Each image tag was checked in its
	registry.

## A live session on Inventor 2026

On 2026-10-29, Claude Code with telemetry on started Inventor 2026, made a plate part with four holes, a pin part and
an assembly with four insert constraints. Prometheus then had 21 calls on release 2026.

- **Tool errors were counted as `ok`.** About 50 places in the tools return `{ error, message }` with no bridge call
	(ex. `inventor_dialogs` with no bridge gave `inventor-not-running`), so `SafeAsync` did not see them. The call tool
	filter now reads the top level `error` of the result, only when the metric has a listener, and records the outcome
	`tool-error`. An `error` inside a list item (one file of several) is not a failure of the call. The code goes out
	only when it is a constant of `BridgeErrorCodes` or of `TelemetryTags.ToolErrorCodes`, and a test fails on a new
	code in the tool sources.
- **A wrong model is `ok`.** The first hole feature drilled in the wrong direction and cut nothing (`DriverLost`), and
	its call was `ok`, because the snippet ran. The outcome measures the API call, not the model. The check that found
	it was the snippet's own count of cylindrical faces.
- **Two snippet traps that the skills do not name yet:** `Feature.Delete(false, ...)` also deletes the consumed
	sketch, and a feature cannot take the name of a sketch (the rename throws `COMException`).
- **The first snippet in a new session took 6.5 s** of main thread time, to create the first part from its template.
	The later snippets took 0.15 to 1.8 s. The first call of a session is the one near the limit of 10 s.
- **The Vault "Log In" dialog** held Inventor 2026 at start. `inventor_start` and the first `inventor_session` wait
	reported only that the bridge did not answer: at this start the add-in loaded after the dialog showed, and the
	dialog tools needed the bridge. The dialog showed only on a later wait. It was recorded as type `none` (not in the
	catalog) and `needs-person`. Later starts showed that the add-in always loads first: at this start it loaded
	only after 1 min 52 s, longer than the wait. See `Docs/Research/Blocking-Dialog-Detection.md`, "The Vault "Log In"
	dialog".
- **Each 2026 snippet result held eight CS1701 warnings** (the add-in's interop reference 30.20 against the installed
	30.40). They are noise in every result.

### What changed after the session

- **Tool errors:** the outcome `tool-error`, as above.
- **A wrong model:** a new counter, `inventor.health.sick_features`, by `inventor.health.status`. `inventor_health`
	records each sick feature that is not suppressed. The status goes out only when it is one of the names that the
	add-in gives (`InventorOperations.Health.cs`), and else as `other`. It does not prove a model right, but a rise in
	`DriverLost` after a change points to a skill or a tool that leads to wrong models.
- **The two traps:** in the `interop` skill, and `Delete(true, false, false)` in the `modeling` skill. Checked again on
	2026 with a part that was not saved: `Delete(true, ...)` kept the sketch (1 sketch left), `Delete(false, ...)`
	removed it (2 sketches before, 1 after), and the rename of a feature to a sketch name threw `COMException`.
- **The first snippet:** the `split-long-work` skill says to create or open the first document of a session in its
	own call. The time of `Documents.Add` comes from Inventor, so the server cannot make it shorter.
- **The Vault dialog:** the type `vaultLogIn` in the catalog (only `"Ask"`). With no pipe, the server takes the process
	from the process list, for the chosen release, when exactly one runs. `inventor_start` then returns
	`waiting-for-user`, and the wait of `inventor_session` stops early with `blocked-by-dialog`. A second start of
	2026 had the bridge first and the dialog 3 to 6 s later: the watchdog found it as `vaultLogIn`. The process list
	path was not reached, because the bridge was already there. See `Docs/Research/Blocking-Dialog-Detection.md`,
	"The Vault "Log In" dialog".
- **CS1701 and CS1702:** the server removes them from each `ExecutionResult`, in `InventorTool.Complete`, so every tool
	that returns a snippet result gets the change. No add-in change.

## Aspire was removed

On 2026-10-29 the Aspire dashboard was removed from the stack, the scripts, Compose and the Collector config.

- With no log export, it showed only traces and metrics, which Tempo, Prometheus and Grafana also show and keep. The
	questions of the dashboard need data from many sessions, which Aspire keeps in memory only.
- It ran with anonymous access (`ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS`), so any process on the machine could
	read it. It was the one part of the stack that took data and had no access control.
- It was one more pinned version, one more exporter in each Collector config, one more Compose service, and the
	ASP.NET Core runtime.
- Full logs in a viewer that keeps them on this machine was the one job that would need it. Of the options (no log
	export, filtered logs to any endpoint, full logs to a local Aspire with sign-in and an OTLP API key, full logs
	through the Collector), no log export is the safest, and it stays.

The server sends standard OTLP, so a user can still point it at Aspire or any other viewer. `Docs/Telemetry.md`,
"Other viewers", says so.

## Counts of short server processes

On 2026-10-29 the dashboard showed about 10 tool calls for the last 7 days, and Prometheus held 53 (the sum of the
last value of each series). Both Vault dialogs counted 0, and p95 was empty for 8 of 10 tools.

- **The cause.** Each server process is its own series (`instance` is `service.instance.id`), and most live for one
	short session. Prometheus scraped the Collector, and a scrape has no start time, so the first sample of a new
	counter was already 1. `increase()` and `rate()` see only a rise between two samples, so the first calls of each
	process did not count. Long running services rarely show this. A stdio server, one process for each session, shows
	it every time.
- **What did not work.** The Collector's `prometheus` exporter writes no `_created` line, even with
	`enable_open_metrics`, so `created-timestamp-zero-ingestion` had nothing to read in a scrape. The feature
	`st-synthesis` drops the first sample, and does not count it.
- **What worked.** A second Prometheus 3.15.1 on a test port, with `--web.enable-otlp-receiver` and
	`--enable-feature=created-timestamp-zero-ingestion`, took OTLP with `StartTimeUnixNano`, and wrote a 0 at the start
	time before the first value. With two test processes (1 call and 3 calls):

	| Query | Result |
	| --- | --- |
	| `sum(increase(inventor_tool_calls_total[30m]))` | 4.38 (extrapolated to the end of the range) |
	| `sum(increase(inventor_tool_calls_total[30m] anchored))` | 4 |
	| `sum(increase(inventor_tool_calls_total[15m] anchored))` | 3 |

	`anchored` needs `--enable-feature=promql-extended-range-selectors`. The real server through a test Collector with
	`otlp_http` to Prometheus gave the same exact counts, the histogram buckets got the 0 too, exemplars kept `trace_id`
	and `span_id`, and `target_info` was still made.
- **The version.** `otlp.promote_resource_attributes: ["service.version"]` puts the version on each metric. Before,
	it was only on `target_info`, so no panel could compare versions.
- **What changed.** The Collector sends metrics to `http://127.0.0.1:9090/api/v1/otlp` (`otlp_http/prometheus`) in
	place of its `prometheus` exporter, and Prometheus scrapes nothing. The Collector stays as the second guard. Each
	count over the range uses `[$__range] anchored`. A pushed series has no expiry, so the connection panel uses
	`last_over_time(...[1m])` in place of the Collector's `metric_expiration: 1m`.
- **Checked on the real stack.** Two server processes through the real Collector and Prometheus (1 and 3
	`inventor_skill` calls, and one unknown tool) gave exactly 4 and 1 with `anchored`. The data from before the change
	stays as it was.

## Names that the client writes

A test server in memory (`LibraryTelemetryTests`) with a tool call, a prompt read and a request, each with a name that
the server does not have:

| Request | What the MCP library 2.3.0 recorded |
| --- | --- |
| `tools/call <unknown>` | The span name `tools/call <unknown>`, `gen_ai.tool.name` on the span, and `gen_ai.tool.name` on `mcp.server.operation.duration`. The server's own `inventor.tool.calls` took the same name from `context.Params.Name`. |
| `prompts/get <unknown>` | The span name `prompts/get <unknown>`. The allowlist removed `gen_ai.prompt.name`, but not the name. |
| An unknown method | The span name and `mcp.method.name`, on the span and the metric. |
| `resources/read <uri>` | The span name `resources/read` only. The allowlist removed `mcp.resource.uri`. |

- The library also adds a second `error.type` (the JSON-RPC code, ex. `-32602`) after the server set its own, so a
	span can have two tags with one key. `SetTag(key, null)` removes one of them.
- `context.MatchedPrimitive` in the call tool filter is null for an unknown tool, and the tool for a known one.
- **What changed.** The filter passes the name only for a matched tool, else `other`. The server keeps the name in a
	custom property of the span, which is not exported. `AllowlistProcessor` then gives each library span its name from
	`TelemetryTags.MethodNameValue` (the constants of `RequestMethods` and `NotificationMethods`) and that tool name. The
	library's meter is not exported, because a view cannot change a value. The Collector sets `other` for a span name,
	a `gen_ai.tool.name` or an `mcp.method.name` that does not have the form of a known name.
- **Checked on the real stack.** An unknown tool named like a path reached Tempo as `tools/call other`, and Prometheus
	as `gen_ai_tool_name="other"`. A span sent straight to the Collector with a path in its name and in both tags
	reached Tempo as `other` in all three.

## Traces with no root span

Tempo showed 4 of 91 traces as "root span not yet received". Each is a `subscriptions/listen` request, which the
client of the 2026-07-28 protocol keeps open for the whole session, so the server can send notifications. The server
log has 10 "called" lines for it and no "completed" line. Its span ends only when the session ends, and a process that
the client stops does not export it. Only its child span, `notifications/subscriptions/acknowledged`, reaches Tempo.
There is one for each session, so nothing changed.

## Other results of the live session

- `inventor_dialogs` with no bridge took 3.0 s on average, the connection timeout. The process list path came after
	these calls, and its time was not measured.
- Average times: `inventor_start` 28 s and `inventor_session` with a wait 6.5 s, both waiting for Inventor. Snippets
	held the main thread 0.6 s on average. The longest wait behind a call of the same server was 0.48 s.
- The server log had 2 warnings, both the Vault dialog.

## Checked live after the changes

On 2026-10-29, after a reconnect to the new build, on Inventor 2026:

- **The sick features.** A part that was not saved, with an extrusion whose sketch lost one line, gave the extrusion
	`CannotCompute`. One `inventor_health` call gave `inventor_health_sick_features_total` with
	`inventor_health_status="CannotCompute"`, `inventor_release="2026"` and `service_version="0.2.0"`, and the panel
	query gave 1.
- **Exact counts.** Each tool call of the session counted once with `anchored`: `inventor_start`, `inventor_dialogs`,
	`inventor_health`, two `inventor_eval_csharp`, and two `inventor_session` (one `ok`, one `tool-error` with
	`error.type="blocked-by-dialog"`).
- **No library metric.** The new server process exported no `mcp_*` series.
- **No CS1701.** Each 2026 snippet result had an empty `diagnostics` list.
- **The Vault dialog.** It always opens after the bridge add-in loads, and the check before a call found it. A restart
	found a bug: the server took the pipe of the closed Inventor as the bridge of the new one. See
	`Docs/Research/Blocking-Dialog-Detection.md`, "The Vault "Log In" dialog".
