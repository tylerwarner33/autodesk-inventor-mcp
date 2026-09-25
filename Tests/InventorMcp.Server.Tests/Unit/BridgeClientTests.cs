using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The response match, the dialog watchdog and the pipe process ID, with a fake add-in, a fake detector and fake time.
/// </summary>
[Trait("Level", "Unit")]
public sealed class BridgeClientTests
{
	private static readonly DialogSnapshot _iLogicError = new(
		0x1234,
		"Error on line 1 in rule: Test, in document: Test.ipt",
		"WindowsForms10.Window.8.app.0.22c9f37_r3_ad1",
		"WinForm",
		"RunExternalRule: Cannot find an external rule file named: \"DoesNotExist\"",
		[new DialogButton("OK", true, true), new DialogButton("Cancel", false, true)]);

	private static readonly DialogSnapshot _question = new(
		0x5678,
		"Autodesk Inventor",
		"#32770",
		"Win32",
		"Save changes to Part1.ipt?",
		[new DialogButton("Yes", true, true), new DialogButton("No", true, true)]);

	[Fact]
	public async Task PipeGivesTheProcessThatHostsIt()
	{
		await using TestBridge bridge = new();
		Task<TestConnection> accept = bridge.Server.AcceptAsync();

		Assert.True(await bridge.Client.TryConnectAsync(TestContext.Current.CancellationToken));
		_ = await accept;

		Assert.Equal(Environment.ProcessId, bridge.Client.InventorProcessId);
	}

	[Fact]
	public async Task LateResponseIsDiscarded()
	{
		await using TestBridge bridge = new();
		(Task<string> call, TestConnection connection, BridgeRequest request) = await bridge.StartPingAsync();

		await connection.RespondAsync("an-older-request", "stale");
		await connection.RespondAsync(request.Id, "fresh");

		Assert.Equal("fresh", await call);
	}

	[Fact]
	public async Task PipeThatClosesAfterALateResponseReconnectsOnce()
	{
		await using TestBridge bridge = new();
		(Task<string> call, TestConnection first, _) = await bridge.StartPingAsync();

		Task<TestConnection> accept = bridge.Server.AcceptAsync();
		await first.RespondAsync("an-older-request", "stale");
		first.Disconnect();

		TestConnection second = await accept;
		BridgeRequest retry = await second.ReadRequestAsync();
		await second.RespondAsync(retry.Id, "fresh");

		Assert.Equal("fresh", await call);
	}

	[Fact]
	public async Task NoDialogCheckBeforeTheFirstInterval()
	{
		await using TestBridge bridge = new();
		(Task<string> call, TestConnection connection, BridgeRequest request) = await bridge.StartPingAsync();

		await bridge.AdvanceAsync(BridgeClient.FirstDialogCheck - TimeSpan.FromMilliseconds(500));
		Assert.Equal(0, bridge.Dialogs.DetectCalls);

		await bridge.AdvanceAsync(TimeSpan.FromMilliseconds(750));
		await TestBridge.WaitUntilAsync(() => bridge.Dialogs.DetectCalls == 1);

		await connection.RespondAsync(request.Id, "done");
		Assert.Equal("done", await call);
	}

	[Fact]
	public async Task SlowCallWithNoDialogReturnsItsResult()
	{
		await using TestBridge bridge = new();
		List<DialogReport> reports = DialogReports.Begin();
		(Task<string> call, TestConnection connection, BridgeRequest request) = await bridge.StartPingAsync();

		await bridge.AdvanceAsync(TimeSpan.FromSeconds(10));
		await TestBridge.WaitUntilAsync(() => bridge.Dialogs.DetectCalls >= 4);
		await connection.RespondAsync(request.Id, "done");

		Assert.Equal("done", await call);
		Assert.Empty(reports);
		Assert.Equal(0, bridge.Dialogs.ClickCalls);
	}

	[Fact]
	public async Task InformationDialogIsClosedAndReported()
	{
		await using TestBridge bridge = new();
		List<DialogReport> reports = DialogReports.Begin();
		(Task<string> call, TestConnection connection, BridgeRequest request) = await bridge.StartPingAsync();
		bridge.Dialogs.Dialogs = [_iLogicError];

		await bridge.AdvanceAsync(BridgeClient.FirstDialogCheck + TimeSpan.FromMilliseconds(250));
		await TestBridge.WaitUntilAsync(() => bridge.Dialogs.ClickCalls == 1);

		// The click frees the main thread, and the call then answers.
		await bridge.AdvanceAsync(BridgeClient.DialogCheckInterval * 2);
		await connection.RespondAsync(request.Id, "done");

		Assert.Equal("done", await call);
		Assert.Equal(1, bridge.Dialogs.ClickCalls);
		DialogReport report = Assert.Single(reports);
		Assert.Equal("iLogic error", report.Type);
		Assert.Equal("closed with OK", report.Action);
		Assert.Contains("DoesNotExist", report.Text);
	}

	[Fact]
	public async Task QuestionStopsTheWaitWithoutAClick()
	{
		await using TestBridge bridge = new();
		List<DialogReport> reports = DialogReports.Begin();
		(Task<string> call, _, _) = await bridge.StartPingAsync();
		bridge.Dialogs.Dialogs = [_question];

		await bridge.AdvanceAsync(BridgeClient.FirstDialogCheck + TimeSpan.FromMilliseconds(250));

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(() => call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
		Assert.Equal(BridgeErrorCodes.BlockedByDialog, exception.Code);
		Assert.Contains("Visible buttons: Yes, No", exception.Message);
		Assert.Equal(_question.Text, exception.Detail);
		Assert.Equal(0, bridge.Dialogs.ClickCalls);
		Assert.Equal("left open for a person", Assert.Single(reports).Action);
	}

	[Fact]
	public async Task NextCallAfterAStoppedWaitGetsItsOwnResponse()
	{
		await using TestBridge bridge = new();
		(Task<string> blocked, TestConnection connection, BridgeRequest first) = await bridge.StartPingAsync();
		bridge.Dialogs.Dialogs = [_question];
		await bridge.AdvanceAsync(BridgeClient.FirstDialogCheck + TimeSpan.FromMilliseconds(250));
		_ = await Assert.ThrowsAsync<InventorBridgeException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

		// The user answers the question. The add-in then answers the first call, which nobody waits for.
		bridge.Dialogs.Dialogs = null;
		(Task<string> next, _, BridgeRequest second) = await bridge.StartPingAsync(connection);
		await connection.RespondAsync(first.Id, "first");
		await connection.RespondAsync(second.Id, "second");

		Assert.Equal("second", await next);
	}

	[Fact]
	public async Task BlockWithNoDialogStopsTheWaitOnTheSecondCheck()
	{
		await using TestBridge bridge = new();
		(Task<string> call, _, _) = await bridge.StartPingAsync();
		bridge.Dialogs.BlockedWithoutDialog = true;

		await bridge.AdvanceAsync(BridgeClient.FirstDialogCheck + TimeSpan.FromMilliseconds(250));
		await TestBridge.WaitUntilAsync(() => bridge.Dialogs.DetectCalls == 1);
		Assert.False(call.IsCompleted);

		await bridge.AdvanceAsync(BridgeClient.DialogCheckInterval);

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(() => call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
		Assert.Equal(BridgeErrorCodes.BlockedByDialog, exception.Code);
	}
}