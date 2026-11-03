using System.Diagnostics;

using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;
using InventorMcp.Server.Telemetry;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The span and the metrics of a bridge round trip, with a fake add-in.
/// </summary>
[Trait("Level", "Unit")]
public sealed class BridgeTelemetryTests
{
	private static readonly DialogSnapshot _iLogicError = new(
		0x1234,
		"Error on line 1 in rule: Test, in document: Test.ipt",
		"WindowsForms10.Window.8.app.0.22c9f37_r3_ad1",
		"WinForm",
		"RunExternalRule: Cannot find an external rule file named: \"DoesNotExist\"",
		[new DialogButton("OK", true, true), new DialogButton("Cancel", false, true)]);

	[Fact]
	public async Task RoundTripIsOneSpanWithItsOperationAndRelease()
	{
		using TelemetryCapture capture = new();
		await using TestBridge bridge = new();

		(Task<string> call, TestConnection connection, BridgeRequest request) = await bridge.StartPingAsync();
		await connection.RespondAsync(request.Id, "done");
		Assert.Equal("done", await call);

		Activity span = Assert.Single(capture.Spans, static span => span.Source.Name == ServerTelemetry.Name);
		Assert.Equal($"bridge {BridgeOperations.Ping}", span.DisplayName);
		Assert.Equal(ActivityKind.Client, span.Kind);
		Assert.Equal(BridgeOperations.Ping, span.GetTagItem(TelemetryTags.BridgeOperation));
		Assert.Equal(TelemetryTags.OutcomeOk, span.GetTagItem(TelemetryTags.Outcome));
		Assert.Equal("2025", span.GetTagItem(TelemetryTags.Release));
		Assert.Empty(span.Events);
	}

	[Fact]
	public async Task RetryAfterADroppedPipeIsOneSpanWithAReconnectEvent()
	{
		using TelemetryCapture capture = new();
		await using TestBridge bridge = new();
		(Task<string> call, TestConnection first, _) = await bridge.StartPingAsync();

		Task<TestConnection> accept = bridge.Server.AcceptAsync();
		first.Disconnect();

		TestConnection second = await accept;
		BridgeRequest retry = await second.ReadRequestAsync();
		await second.RespondAsync(retry.Id, "fresh");
		Assert.Equal("fresh", await call);

		Activity span = Assert.Single(capture.Spans, static span => span.Source.Name == ServerTelemetry.Name);
		Assert.Equal("reconnect", Assert.Single(span.Events).Name);
		Assert.Equal(TelemetryTags.OutcomeOk, span.GetTagItem(TelemetryTags.Outcome));
	}

	[Fact]
	public async Task ClickedDialogIsCountedByTypeAndAction()
	{
		using TelemetryCapture capture = new();
		await using TestBridge bridge = new();
		_ = DialogReports.Begin();
		(Task<string> call, TestConnection connection, BridgeRequest request) = await bridge.StartPingAsync();
		bridge.Dialogs.Dialogs = [_iLogicError];

		await bridge.AdvanceAsync(BridgeClient.FirstDialogCheck + TimeSpan.FromMilliseconds(250));
		await TestBridge.WaitUntilAsync(() => bridge.Dialogs.ClickCalls == 1);
		await bridge.AdvanceAsync(BridgeClient.DialogCheckInterval * 2);
		await connection.RespondAsync(request.Id, "done");
		Assert.Equal("done", await call);

		Assert.Contains(
			capture.Measurements("inventor.dialogs.answered"),
			static measurement => measurement.Tag(TelemetryTags.DialogType) == DialogPolicy.ILogicErrorType
				&& measurement.Tag(TelemetryTags.DialogAction) == TelemetryTags.DialogClicked);

		// Never the title or the text of the dialog.
		Assert.DoesNotContain(
			capture.Measurements("inventor.dialogs.answered").SelectMany(static measurement => measurement.Tags.Values),
			static value => value?.Contains("DoesNotExist", StringComparison.Ordinal) is true || value?.Contains("Test.ipt", StringComparison.Ordinal) is true);
	}
}
