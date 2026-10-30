using System.Diagnostics;

using InventorMcp.Contracts;
using InventorMcp.Server.Telemetry;

using ModelContextProtocol.Protocol;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	One tool call gives one count and one duration, with the tags of its outcome, on the metrics and on its span.
/// </summary>
[Trait("Level", "Unit")]
public sealed class ServerTelemetryTests
{
	[Fact]
	public async Task SuccessIsOk()
	{
		using TelemetryCapture capture = new();
		string tool = UniqueTool();

		Activity span = await RunAsync(tool, static () => ValueTask.FromResult(new CallToolResult()));

		CapturedMeasurement call = AssertOneCall(capture, tool, TelemetryTags.OutcomeOk);
		Assert.Null(call.Tag(TelemetryTags.ErrorType));
		Assert.Equal("2026", call.Tag(TelemetryTags.Release));
		Assert.Equal("test-client 1.0", call.Tag(TelemetryTags.ClientName));
		Assert.Equal(TelemetryTags.OutcomeOk, span.GetTagItem(TelemetryTags.Outcome));
		Assert.Equal("chosen", span.GetTagItem(TelemetryTags.ReleaseSelection));
		Assert.Equal(ActivityStatusCode.Unset, span.Status);
	}

	[Fact]
	public async Task BridgeErrorGivesItsCode()
	{
		using TelemetryCapture capture = new();
		string tool = UniqueTool();

		Activity span = await RunAsync(tool, static () =>
		{
			// What InventorTool.SafeAsync does when it catches an InventorBridgeException.
			_ = ToolOutcome.Current!.Fail(TelemetryTags.OutcomeBridgeError, TelemetryTags.BridgeErrorValue(BridgeErrorCodes.NotRunning));

			return ValueTask.FromResult(new CallToolResult());
		});

		CapturedMeasurement call = AssertOneCall(capture, tool, TelemetryTags.OutcomeBridgeError);
		Assert.Equal(BridgeErrorCodes.NotRunning, call.Tag(TelemetryTags.ErrorType));
		Assert.Equal(BridgeErrorCodes.NotRunning, span.GetTagItem(TelemetryTags.ErrorType));
		Assert.Equal(ActivityStatusCode.Error, span.Status);
	}

	[Fact]
	public async Task FailedSnippetGivesItsTypeAndItsMainThreadTime()
	{
		using TelemetryCapture capture = new();
		string tool = UniqueTool();

		_ = await RunAsync(tool, static () =>
		{
			ToolOutcome.Current!.MainThreadMilliseconds = 1500;
			_ = ToolOutcome.Current.Fail(
				TelemetryTags.OutcomeCodeFailed,
				TelemetryTags.ExceptionTypeValue("System.Runtime.InteropServices.COMException"));

			return ValueTask.FromResult(new CallToolResult());
		});

		CapturedMeasurement call = AssertOneCall(capture, tool, TelemetryTags.OutcomeCodeFailed);
		Assert.Equal("System.Runtime.InteropServices.COMException", call.Tag(TelemetryTags.ErrorType));

		CapturedMeasurement mainThread = Assert.Single(capture.Measurements("inventor.execution.main_thread"), measurement => measurement.Tag(TelemetryTags.ToolName) == tool);
		Assert.InRange(mainThread.Value, 1.4999, 1.5001);
	}

	[Fact]
	public async Task ThrowGivesTheTypeAndNotTheMessage()
	{
		using TelemetryCapture capture = new();
		string tool = UniqueTool();

		_ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			RunAsync(tool, static () => throw new InvalidOperationException(@"C:\Clients\Secret\Part.ipt")));

		CapturedMeasurement call = AssertOneCall(capture, tool, TelemetryTags.OutcomeException);
		Assert.Equal("System.InvalidOperationException", call.Tag(TelemetryTags.ErrorType));

		Activity toolSpan = Assert.Single(capture.Spans, recorded => recorded.DisplayName == $"tools/call {tool}");
		Assert.Equal("System.InvalidOperationException", toolSpan.GetTagItem(TelemetryTags.ErrorType));
		Assert.DoesNotContain(toolSpan.TagObjects, tag => tag.Value?.ToString()?.Contains("Secret", StringComparison.Ordinal) is true);
	}

	[Fact]
	public async Task IsErrorResultIsAnException()
	{
		using TelemetryCapture capture = new();
		string tool = UniqueTool();

		_ = await RunAsync(tool, static () => ValueTask.FromResult(new CallToolResult { IsError = true }));

		CapturedMeasurement call = AssertOneCall(capture, tool, TelemetryTags.OutcomeException);
		Assert.Equal(TelemetryTags.ToolError, call.Tag(TelemetryTags.ErrorType));
	}

	[Theory]
	[InlineData("""{"error":"invalid-arguments","message":"Give at least one path."}""", "invalid-arguments")]
	[InlineData("""{"error":"inventor-not-running","message":"No Inventor session."}""", "inventor-not-running")]
	[InlineData("""{"error":"C:\\Clients\\Secret.ipt","message":"x"}""", TelemetryTags.Other)]
	public async Task ErrorThatAToolReturnsIsAToolError(string text, string errorType)
	{
		using TelemetryCapture capture = new();
		string tool = UniqueTool();

		_ = await RunAsync(tool, () => ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = text }] }));

		CapturedMeasurement call = AssertOneCall(capture, tool, TelemetryTags.OutcomeToolError);
		Assert.Equal(errorType, call.Tag(TelemetryTags.ErrorType));
	}

	[Theory]
	[InlineData("""{"results":[{"file":"a.ipt","error":"locked"}]}""")]
	[InlineData("""{"status":"still-starting"}""")]
	[InlineData("""not json "error" """)]
	[InlineData("""["error"]""")]
	public async Task ErrorBelowTheTopLevelIsOk(string text)
	{
		using TelemetryCapture capture = new();
		string tool = UniqueTool();

		_ = await RunAsync(tool, () => ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = text }] }));

		_ = AssertOneCall(capture, tool, TelemetryTags.OutcomeOk);
	}

	[Fact]
	public async Task BridgeErrorIsNotCountedAgainAsAToolError()
	{
		using TelemetryCapture capture = new();
		string tool = UniqueTool();

		_ = await RunAsync(tool, static () =>
		{
			_ = ToolOutcome.Current!.Fail(TelemetryTags.OutcomeBridgeError, BridgeErrorCodes.Busy);

			return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = """{"error":"inventor-busy"}""" }] });
		});

		_ = AssertOneCall(capture, tool, TelemetryTags.OutcomeBridgeError);
	}

	[Fact]
	public void SkillReadCountsTheName()
	{
		using TelemetryCapture capture = new();

		ServerTelemetry.RecordSkillRead("interop");

		Assert.Contains(capture.Measurements("inventor.skill.reads"), measurement => measurement.Tag(TelemetryTags.SkillName) == "interop");
	}

	[Fact]
	public void HealthCountsEachSickFeatureByStatus()
	{
		using TelemetryCapture capture = new();

		ServerTelemetry.RecordHealth(["DriverLost", "DriverLost", @"C:\Clients\Secret.ipt"], 2027);

		List<CapturedMeasurement> sick = [.. capture.Measurements("inventor.health.sick_features").Where(static measurement => measurement.Tag(TelemetryTags.Release) == "2027")];
		Assert.Equal(2, sick.Count(static measurement => measurement.Tag(TelemetryTags.HealthStatus) == "DriverLost"));
		_ = Assert.Single(sick, static measurement => measurement.Tag(TelemetryTags.HealthStatus) == TelemetryTags.Other);
	}

	[Fact]
	public void ConnectionGaugeGivesTheRelease()
	{
		using TelemetryCapture capture = new();
		Func<(bool, int?)>? before = ServerTelemetry.ConnectionState;

		try
		{
			ServerTelemetry.ConnectionState = static () => (true, 2027);
			capture.Observe();
		}
		finally
		{
			ServerTelemetry.ConnectionState = before;
		}

		Assert.Contains(
			capture.Measurements("inventor.bridge.connected"),
			static measurement => measurement.Value > 0.5 && measurement.Tag(TelemetryTags.Release) == "2027");
	}

	private static string UniqueTool() => $"test_tool_{Guid.NewGuid():N}";

	/// <summary>
	/// 	Runs one call under a span that takes the place of the span of the MCP library.
	/// </summary>
	private static async Task<Activity> RunAsync(string tool, Func<ValueTask<CallToolResult>> call)
	{
		Activity span = TelemetryCapture.TestSource.StartActivity($"tools/call {tool}", ActivityKind.Server)!;

		try
		{
			_ = await ServerTelemetry.RunToolCallAsync(call, tool, static () => (2026, "test-client 1.0", "chosen"));
		}
		finally
		{
			span.Stop();
		}

		return span;
	}

	private static CapturedMeasurement AssertOneCall(TelemetryCapture capture, string tool, string outcome)
	{
		CapturedMeasurement call = Assert.Single(capture.Measurements("inventor.tool.calls"), measurement => measurement.Tag(TelemetryTags.ToolName) == tool);
		Assert.Equal(1, call.Value);
		Assert.Equal(outcome, call.Tag(TelemetryTags.Outcome));

		CapturedMeasurement duration = Assert.Single(capture.Measurements("inventor.tool.duration"), measurement => measurement.Tag(TelemetryTags.ToolName) == tool);
		Assert.Equal("s", duration.Unit);
		Assert.Equal(outcome, duration.Tag(TelemetryTags.Outcome));

		return call;
	}
}
