using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;

using ModelContextProtocol.Protocol;

namespace InventorMcp.Server.Telemetry;

/// <summary>
/// 	The traces and metrics of the server, through the .NET APIs only.
/// </summary>
/// <remarks>
/// 	Only <see cref="TelemetryRegistration"/> knows about OpenTelemetry.
/// 	With no listener, a span is not started and a measurement goes nowhere, so this costs almost nothing when telemetry
/// 	is off.
/// 	See <c>Docs/Telemetry.md</c> for the questions each instrument answers.
/// </remarks>
internal static class ServerTelemetry
{
	/// <summary>
	/// 	The name of the server's <see cref="ActivitySource"/> and <see cref="Meter"/>, and the service name.
	/// </summary>
	public const string Name = "InventorMcp.Server";

	/// <summary>
	/// 	The name of the source and the meter of the MCP library.
	/// </summary>
	/// <remarks>
	/// 	The name starts with <c>Experimental.</c>, so a later version can change it.
	/// 	<c>LibraryTelemetryTests</c> fails when it does.
	/// </remarks>
	public const string LibrarySourceName = "Experimental.ModelContextProtocol";

	/// <summary>
	/// 	The custom property of a library span that holds the tool name that the server sends.
	/// </summary>
	/// <remarks>
	/// 	The library writes the client's tool name on the span after the call, so <see cref="AllowlistProcessor"/> replaces
	/// 	it with this value at the end. A custom property is not exported.
	/// </remarks>
	public const string ToolNameProperty = "InventorMcpToolName";

	/// <summary>
	/// 	The bucket boundaries of each duration histogram, in seconds, from the MCP semantic conventions.
	/// </summary>
	/// <remarks>
	/// 	The MCP library uses the same, so its durations and the server's can be compared.
	/// </remarks>
	public static readonly double[] DurationBoundaries = [0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 30, 60, 120, 300];

	/// <summary>
	/// 	The version of the server, ex. <c>0.2.0</c>.
	/// </summary>
	public static string Version { get; } = ReadVersion();

	/// <summary>
	/// 	The source of the server's own spans.
	/// </summary>
	public static ActivitySource Source { get; } = new(Name, Version);

	private static readonly Meter _meter = new(Name, Version);

	private static readonly Counter<long> _toolCalls = _meter.CreateCounter<long>(
		"inventor.tool.calls", "{call}", "Tool calls, by outcome.");

	private static readonly Histogram<double> _toolDuration = _meter.CreateHistogram<double>(
		"inventor.tool.duration", "s", "The time of a tool call, from the request to the result.");

	private static readonly Histogram<double> _bridgeDuration = _meter.CreateHistogram<double>(
		"inventor.bridge.duration", "s", "The time of a bridge round trip, after the wait for the bridge.");

	private static readonly Histogram<double> _queueWait = _meter.CreateHistogram<double>(
		"inventor.bridge.queue_wait", "s", "The time a call waited for an earlier call of this server to finish.");

	private static readonly Histogram<double> _mainThread = _meter.CreateHistogram<double>(
		"inventor.execution.main_thread", "s", "The time a snippet held Inventor's main thread.");

	private static readonly Counter<long> _skillReads = _meter.CreateCounter<long>(
		"inventor.skill.reads", "{read}", "Skill files read.");

	private static readonly Counter<long> _dialogs = _meter.CreateCounter<long>(
		"inventor.dialogs.answered", "{dialog}", "Blocking dialogs that the server found while a call waited, by what it did.");

	private static readonly Counter<long> _sickFeatures = _meter.CreateCounter<long>(
		"inventor.health.sick_features", "{feature}", "Sick features that inventor_health reported, by health status. Suppressed features are not counted.");

	/// <summary>
	/// 	Gives whether the bridge is connected, and to which release, for the gauge <c>inventor.bridge.connected</c>.
	/// </summary>
	/// <remarks>
	/// 	Set at start. Null gives no measurement.
	/// </remarks>
	public static Func<(bool Connected, int? ReleaseYear)>? ConnectionState { get; set; }

	static ServerTelemetry() =>
		_ = _meter.CreateObservableGauge(
			"inventor.bridge.connected",
			ObserveConnection,
			"{server}",
			"1 when this server's pipe is connected, else 0. The sum over the servers of one release is the number of its connections.");

	/// <summary>
	/// 	Runs one tool call, and records its outcome and its time.
	/// </summary>
	/// <remarks>
	/// 	The call tool filter in <c>Program.cs</c> calls it.
	/// 	<see cref="Activity.Current"/> is then the span of the MCP library for the call. The server adds its tags to it,
	/// 	and does not start a second span for the same call.
	/// </remarks>
	/// <param name="call">
	/// 	The rest of the call, down to the tool.
	/// </param>
	/// <param name="tool">
	/// 	The name of a tool that the server has, or null for a name that it does not have, which is sent as
	/// 	<see cref="TelemetryTags.Other"/>.
	/// </param>
	/// <param name="state">
	/// 	Gives the release, the client and the release selection after the call, which can change them.
	/// </param>
	/// <returns>
	/// 	The result of the call.
	/// </returns>
	public static async ValueTask<CallToolResult> RunToolCallAsync(
		Func<ValueTask<CallToolResult>> call,
		string? tool,
		Func<(int? ReleaseYear, string? ClientName, string Selection)> state)
	{
		Activity? span = Activity.Current;
		ToolOutcome outcome = ToolOutcome.Begin();
		long started = Stopwatch.GetTimestamp();

		try
		{
			CallToolResult result = await call().ConfigureAwait(false);

			if (outcome.Outcome == TelemetryTags.OutcomeOk)
			{
				if (result.IsError is true)
					_ = outcome.Fail(TelemetryTags.OutcomeException, TelemetryTags.ToolError);
				// Only with a listener, so the result is never parsed when telemetry is off.
				else if (_toolCalls.Enabled && ReportedError(result) is string code)
					_ = outcome.Fail(TelemetryTags.OutcomeToolError, TelemetryTags.ToolErrorValue(code));
			}

			return result;
		}
		// Records the type and does not catch the exception. Never the message, which can hold a path.
		catch (Exception exception) when (outcome.Fail(TelemetryTags.OutcomeException, TelemetryTags.ExceptionTypeValue(exception.GetType().FullName)))
		{
			throw;
		}
		finally
		{
			(int? releaseYear, string? clientName, string selection) = state();

			RecordToolCall(span, tool, outcome, Stopwatch.GetElapsedTime(started), releaseYear, clientName, selection);
		}
	}

	/// <summary>
	/// 	Records one tool call, on the span of the MCP library and in the metrics.
	/// </summary>
	/// <param name="span">
	/// 	The span of the tool call, or null.
	/// </param>
	/// <param name="tool">
	/// 	The name of a tool that the server has, or null.
	/// </param>
	/// <param name="outcome">
	/// 	The outcome.
	/// </param>
	/// <param name="duration">
	/// 	The time from the request to the result.
	/// </param>
	/// <param name="releaseYear">
	/// 	The connected release, or null.
	/// </param>
	/// <param name="clientName">
	/// 	The MCP client, or null.
	/// </param>
	/// <param name="selection">
	/// 	How the session chose its release.
	/// </param>
	internal static void RecordToolCall(
		Activity? span,
		string? tool,
		ToolOutcome outcome,
		TimeSpan duration,
		int? releaseYear,
		string? clientName,
		string selection)
	{
		string toolName = tool ?? TelemetryTags.Other;
		string release = TelemetryTags.ReleaseValue(releaseYear);

		if (span is not null)
		{
			span.SetCustomProperty(ToolNameProperty, toolName);

			_ = span
				.SetTag(TelemetryTags.Outcome, outcome.Outcome)
				.SetTag(TelemetryTags.Release, release)
				.SetTag(TelemetryTags.ReleaseSelection, selection)
				.SetTag(TelemetryTags.ClientName, TelemetryTags.ClientNameValue(clientName));

			if (outcome.ErrorType is not null)
			{
				_ = span
					.SetTag(TelemetryTags.ErrorType, outcome.ErrorType)
					.SetStatus(ActivityStatusCode.Error);
			}
		}

		TagList callTags = new()
		{
			{ TelemetryTags.ToolName, toolName },
			{ TelemetryTags.Outcome, outcome.Outcome },
			{ TelemetryTags.Release, release },
			{ TelemetryTags.ClientName, TelemetryTags.ClientNameValue(clientName) }
		};

		if (outcome.ErrorType is not null)
			callTags.Add(TelemetryTags.ErrorType, outcome.ErrorType);

		_toolCalls.Add(1, callTags);

		_toolDuration.Record(
			duration.TotalSeconds,
			new TagList
			{
				{ TelemetryTags.ToolName, toolName },
				{ TelemetryTags.Outcome, outcome.Outcome },
				{ TelemetryTags.Release, release }
			});

		if (outcome.MainThreadMilliseconds is long mainThread)
		{
			_mainThread.Record(
				mainThread / 1000.0,
				new TagList { { TelemetryTags.ToolName, toolName }, { TelemetryTags.Release, release } });
		}
	}

	/// <summary>
	/// 	Starts the span of one bridge round trip, or returns null with no listener.
	/// </summary>
	public static Activity? StartBridgeCall(string operation)
	{
		string name = TelemetryTags.BridgeOperationValue(operation);

		return Source.StartActivity($"bridge {name}", ActivityKind.Client)?.SetTag(TelemetryTags.BridgeOperation, name);
	}

	/// <summary>
	/// 	Records the end of one bridge round trip, on its span and in the metrics.
	/// </summary>
	/// <param name="span">
	/// 	The span from <see cref="StartBridgeCall"/>, or null.
	/// </param>
	/// <param name="operation">
	/// 	The operation name.
	/// </param>
	/// <param name="outcome">
	/// 	A value of <c>TelemetryTags.Outcome*</c>.
	/// </param>
	/// <param name="errorType">
	/// 	A value that is safe to export, or null when the outcome is ok.
	/// </param>
	/// <param name="duration">
	/// 	The time of the round trip.
	/// </param>
	/// <param name="releaseYear">
	/// 	The connected release, or null.
	/// </param>
	public static void RecordBridgeCall(Activity? span, string operation, string outcome, string? errorType, TimeSpan duration, int? releaseYear)
	{
		string release = TelemetryTags.ReleaseValue(releaseYear);

		if (span is not null)
		{
			_ = span.SetTag(TelemetryTags.Outcome, outcome).SetTag(TelemetryTags.Release, release);

			if (errorType is not null)
				_ = span.SetTag(TelemetryTags.ErrorType, errorType).SetStatus(ActivityStatusCode.Error);
		}

		_bridgeDuration.Record(
			duration.TotalSeconds,
			new TagList
			{
				{ TelemetryTags.BridgeOperation, TelemetryTags.BridgeOperationValue(operation) },
				{ TelemetryTags.Outcome, outcome },
				{ TelemetryTags.Release, release }
			});
	}

	/// <summary>
	/// 	Records the time that a bridge call waited for an earlier call of this server.
	/// </summary>
	public static void RecordQueueWait(TimeSpan wait, int? releaseYear) =>
		_queueWait.Record(wait.TotalSeconds, new KeyValuePair<string, object?>(TelemetryTags.Release, TelemetryTags.ReleaseValue(releaseYear)));

	/// <summary>
	/// 	Records one skill read.
	/// </summary>
	/// <param name="skill">
	/// 	A name from the skill catalog, never the raw argument.
	/// </param>
	public static void RecordSkillRead(string skill)
	{
		_ = Activity.Current?.SetTag(TelemetryTags.SkillName, skill);
		_skillReads.Add(1, new KeyValuePair<string, object?>(TelemetryTags.SkillName, skill));
	}

	/// <summary>
	/// 	Records one blocking dialog that the server found.
	/// </summary>
	/// <param name="type">
	/// 	The catalog type, or null for a type that is not in the catalog.
	/// </param>
	/// <param name="action">
	/// 	A value of <c>TelemetryTags.Dialog*</c>.
	/// </param>
	public static void RecordDialog(string? type, string action) =>
		_dialogs.Add(
			1,
			new KeyValuePair<string, object?>(TelemetryTags.DialogType, type ?? TelemetryTags.None),
			new KeyValuePair<string, object?>(TelemetryTags.DialogAction, action));

	/// <summary>
	/// 	Records the sick features of one health read.
	/// </summary>
	/// <remarks>
	/// 	A call that works can leave a wrong model (ex. a hole that cut nothing gives <c>DriverLost</c>), so its outcome is
	/// 	still ok. This count is the signal of a wrong model.
	/// </remarks>
	/// <param name="statuses">
	/// 	The health status of each sick feature that is not suppressed.
	/// </param>
	/// <param name="releaseYear">
	/// 	The connected release, or null.
	/// </param>
	public static void RecordHealth(IEnumerable<string> statuses, int? releaseYear)
	{
		if (_sickFeatures.Enabled is false)
			return;

		string release = TelemetryTags.ReleaseValue(releaseYear);

		foreach (string status in statuses)
		{
			_sickFeatures.Add(
				1,
				new KeyValuePair<string, object?>(TelemetryTags.HealthStatus, TelemetryTags.HealthStatusValue(status)),
				new KeyValuePair<string, object?>(TelemetryTags.Release, release));
		}
	}

	/// <summary>
	/// 	The top level <c>error</c> code of a tool result, or null.
	/// </summary>
	/// <remarks>
	/// 	Many tools return a failure as a normal result, ex. <c>{ error = "invalid-arguments", message }</c>, with no
	/// 	bridge call, so <c>SafeAsync</c> does not see it. An <c>error</c> inside a list item is part of a result that
	/// 	worked, ex. one file of several, so only the top level counts.
	/// </remarks>
	internal static string? ReportedError(CallToolResult result)
	{
		foreach (ContentBlock block in result.Content)
		{
			if (block is not TextContentBlock { Text: { } text } || text.StartsWith('{') is false || text.Contains("\"error\"", StringComparison.Ordinal) is false)
				continue;

			try
			{
				using JsonDocument document = JsonDocument.Parse(text);

				if (document.RootElement.TryGetProperty("error", out JsonElement error) && error.ValueKind is JsonValueKind.String)
					return error.GetString();
			}
			catch (JsonException)
			{
				// Text that only looks like JSON is not a failure.
			}
		}

		return null;
	}

	private static IEnumerable<Measurement<int>> ObserveConnection()
	{
		if (ConnectionState is not { } state)
			yield break;

		(bool connected, int? releaseYear) = state();

		yield return new Measurement<int>(
			connected ? 1 : 0,
			new KeyValuePair<string, object?>(TelemetryTags.Release, TelemetryTags.ReleaseValue(releaseYear)));
	}

	private static string ReadVersion()
	{
		string? version = typeof(ServerTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

		// The part after '+' is the commit, which the version of a build from the local feed already makes unique.
		return version is null ? "unknown" : version.Split('+')[0];
	}
}
