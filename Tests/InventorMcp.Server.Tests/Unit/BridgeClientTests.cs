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

	private static readonly DialogSnapshot _dotNetError = new(
		0x9ABC,
		"Microsoft .NET",
		"WindowsForms10.Window.8.app.0.22c9f37_r3_ad1",
		"WinForm",
		"Unhandled exception has occurred in a component in your application.\r\n\r\nCannot access a disposed object.\r\n" +
			"Object name: 'DevExpress.XtraTab.XtraTabPage'.",
		[new DialogButton("Details", true, true), new DialogButton("Continue", true, true)]);

	private static readonly DialogSnapshot _downloadProgress = new(
		0x2468,
		"Download progress",
		"WindowsForms10.Window.8.app.0.6fbc03_r3_ad1",
		"WinForm",
		"Preparing Files",
		[new DialogButton("Cancel", true, true)]);

	private static readonly DialogSnapshot _question = new(
		0x5678,
		"Autodesk Inventor",
		"#32770",
		"Win32",
		"Save changes to Part1.ipt?",
		[new DialogButton("Yes", true, true), new DialogButton("No", true, true)]);

	[Fact]
	public async Task PipeOfAProcessThatIsNotInventorIsRefused()
	{
		int? checkedProcess = null;

		await using TestBridge bridge = new(processId =>
		{
			checkedProcess = processId;
			return "It is not Inventor.";
		});

		bridge.Server.Listen();

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(
			() => bridge.Client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken));

		Assert.Equal(BridgeErrorCodes.UntrustedHost, exception.Code);
		Assert.Contains("It is not Inventor.", exception.Message);
		Assert.Equal(Environment.ProcessId, checkedProcess);
		Assert.Null(bridge.Client.InventorProcessId);
	}

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
	public async Task ChangeOfTheChoiceResetsTheReleaseAndTheProcess()
	{
		await using TestBridge bridge = new();
		Task<TestConnection> accept = bridge.Server.AcceptAsync();

		Assert.True(await bridge.Client.TryConnectAsync(TestContext.Current.CancellationToken));
		_ = await accept;

		Assert.Equal(2025, bridge.Client.ReleaseYear);
		_ = Assert.NotNull(bridge.Client.InventorProcessId);

		// Release 2026 has no pipe, so the next connection fails. The old connection must not survive the change.
		bridge.Selection.Choose(2026);

		Assert.False(await bridge.Client.TryConnectAsync(TestContext.Current.CancellationToken));
		Assert.Null(bridge.Client.ReleaseYear);
		Assert.Null(bridge.Client.InventorProcessId);
	}

	[Fact]
	public async Task AFailureOfTheAddInGivesItsBridgeCode()
	{
		await using TestBridge bridge = new();
		(Task<string> call, TestConnection connection, TestRequest request) = await bridge.StartCallAsync();

		await connection.FailAsync(request.Id, BridgeErrorCodes.Busy, "Inventor is busy.");

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(() => call);
		Assert.Equal(BridgeErrorCodes.Busy, exception.Code);
		Assert.Equal("Inventor is busy.", exception.Message);
	}

	[Fact]
	public async Task PipeThatClosesDuringACallReconnectsOnce()
	{
		await using TestBridge bridge = new();
		(Task<string> call, TestConnection first, _) = await bridge.StartCallAsync();

		Task<TestConnection> accept = bridge.Server.AcceptAsync();
		first.Disconnect();

		TestConnection second = await accept;
		TestRequest retry = await second.ReadRequestAsync();
		await second.RespondAsync(retry.Id, "fresh");

		Assert.Equal("fresh", await call);
	}

	[Fact]
	public async Task ACancelOfTheCallReachesTheAddIn()
	{
		await using TestBridge bridge = new();
		using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		(Task<string> call, TestConnection connection, TestRequest request) = await bridge.StartCallAsync(cancellationToken: cancel.Token);

		await cancel.CancelAsync();

		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
		await TestBridge.WaitUntilAsync(() => connection.Canceled.Contains(request.Id));
	}

	[Fact]
	public async Task ActivityDoesNotWaitBehindACallThatUsesTheMainThread()
	{
		await using TestBridge bridge = new();
		(Task<string> blocked, TestConnection connection, TestRequest first) = await bridge.StartCallAsync();

		Task<string> activity = bridge.Client.InvokeAsync<string>(BridgeOperations.Activity, null, TestContext.Current.CancellationToken);
		TestRequest second = await connection.ReadRequestAsync();
		await connection.RespondAsync(second.Id, "events");

		Assert.Equal(BridgeOperations.Activity, second.Operation);
		Assert.Equal("events", await activity);
		Assert.False(blocked.IsCompleted);

		await connection.RespondAsync(first.Id, "done");
		Assert.Equal("done", await blocked);
	}

	[Fact]
	public async Task NoDialogCheckBeforeTheFirstInterval()
	{
		await using TestBridge bridge = new();
		(Task<string> call, TestConnection connection, TestRequest request) = await bridge.StartCallAsync();

		// One check runs before the request is sent.
		Assert.Equal(1, bridge.Dialogs.DetectCalls);

		await bridge.AdvanceAsync(BridgeClient.FirstDialogCheck - TimeSpan.FromMilliseconds(500));
		Assert.Equal(1, bridge.Dialogs.DetectCalls);

		await bridge.AdvanceAsync(TimeSpan.FromMilliseconds(750));
		await TestBridge.WaitUntilAsync(() => bridge.Dialogs.DetectCalls == 2);

		await connection.RespondAsync(request.Id, "done");
		Assert.Equal("done", await call);
	}

	[Fact]
	public async Task SlowCallWithNoDialogReturnsItsResult()
	{
		await using TestBridge bridge = new();
		List<DialogReport> reports = DialogReports.Begin();
		(Task<string> call, TestConnection connection, TestRequest request) = await bridge.StartCallAsync();

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
		(Task<string> call, TestConnection connection, TestRequest request) = await bridge.StartCallAsync();
		bridge.Dialogs.Dialogs = [_iLogicError];

		await bridge.AdvanceAsync(BridgeClient.FirstDialogCheck + TimeSpan.FromMilliseconds(250));
		await TestBridge.WaitUntilAsync(() => bridge.Dialogs.ClickCalls == 1);

		// The click frees the main thread, and the call then answers.
		await bridge.AdvanceAsync(BridgeClient.DialogCheckInterval * 2);
		await connection.RespondAsync(request.Id, "done");

		Assert.Equal("done", await call);
		Assert.Equal(1, bridge.Dialogs.ClickCalls);
		DialogReport report = Assert.Single(reports);
		Assert.Equal("iLogicError", report.Type);
		Assert.Equal("closed with OK", report.Action);
		Assert.Contains("DoesNotExist", report.Text);
	}

	[Fact]
	public async Task DotNetErrorIsClosedWithContinue()
	{
		await using TestBridge bridge = new();
		List<DialogReport> reports = DialogReports.Begin();
		(Task<string> call, TestConnection connection, TestRequest request) = await bridge.StartCallAsync();
		bridge.Dialogs.Dialogs = [_dotNetError];

		await bridge.AdvanceAsync(BridgeClient.FirstDialogCheck + TimeSpan.FromMilliseconds(250));
		await TestBridge.WaitUntilAsync(() => bridge.Dialogs.ClickCalls == 1);

		await bridge.AdvanceAsync(BridgeClient.DialogCheckInterval * 2);
		await connection.RespondAsync(request.Id, "done");

		Assert.Equal("done", await call);
		DialogReport report = Assert.Single(reports);
		Assert.Equal("dotNetDisposedObjectError", report.Type);
		Assert.Equal("closed with Continue", report.Action);
	}

	[Fact]
	public async Task QuestionStopsTheWaitWithoutAClick()
	{
		await using TestBridge bridge = new();
		List<DialogReport> reports = DialogReports.Begin();
		(Task<string> call, _, _) = await bridge.StartCallAsync();
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
		(Task<string> blocked, TestConnection connection, TestRequest first) = await bridge.StartCallAsync();
		bridge.Dialogs.Dialogs = [_question];
		await bridge.AdvanceAsync(BridgeClient.FirstDialogCheck + TimeSpan.FromMilliseconds(250));
		_ = await Assert.ThrowsAsync<InventorBridgeException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

		// The user answers the question. The add-in then answers the first call, which nobody waits for.
		bridge.Dialogs.Dialogs = null;
		(Task<string> next, _, TestRequest second) = await bridge.StartCallAsync(connection);
		await connection.RespondAsync(first.Id, "first");
		await connection.RespondAsync(second.Id, "second");

		Assert.Equal("second", await next);
	}

	[Fact]
	public async Task NothingIsSentWhileAQuestionIsOpen()
	{
		await using TestBridge bridge = new();
		Task<TestConnection> accept = bridge.Server.AcceptAsync();
		Assert.True(await bridge.Client.TryConnectAsync(TestContext.Current.CancellationToken));
		TestConnection connection = await accept;
		bridge.Dialogs.Dialogs = [_question];

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(
			() => bridge.Client.InvokeAsync<string>(BridgeOperations.Session, null, TestContext.Current.CancellationToken));

		Assert.Equal(BridgeErrorCodes.BlockedByDialog, exception.Code);
		_ = await Assert.ThrowsAsync<TimeoutException>(() => connection.ReadRequestAsync(TimeSpan.FromMilliseconds(500)));
	}

	[Fact]
	public async Task InformationDialogIsClosedBeforeTheRequestIsSent()
	{
		await using TestBridge bridge = new();
		Task<TestConnection> accept = bridge.Server.AcceptAsync();
		Assert.True(await bridge.Client.TryConnectAsync(TestContext.Current.CancellationToken));
		TestConnection connection = await accept;
		bridge.Dialogs.Dialogs = [_iLogicError];

		(Task<string> call, _, TestRequest request) = await bridge.StartCallAsync(connection);
		await connection.RespondAsync(request.Id, "done");

		Assert.Equal("done", await call);
		Assert.Equal(1, bridge.Dialogs.ClickCalls);
	}

	[Fact]
	public async Task ProgressDialogKeepsTheWaitWithoutAClick()
	{
		await using TestBridge bridge = new();
		List<DialogReport> reports = DialogReports.Begin();
		(Task<string> call, TestConnection connection, TestRequest request) = await bridge.StartCallAsync();
		bridge.Dialogs.Dialogs = [_downloadProgress];

		await bridge.AdvanceAsync(BridgeClient.FirstDialogCheck + (BridgeClient.DialogCheckInterval * 3));
		Assert.False(call.IsCompleted);

		// The download ends, the dialog closes by itself, and the call then answers.
		bridge.Dialogs.Dialogs = null;
		await connection.RespondAsync(request.Id, "done");

		Assert.Equal("done", await call);
		Assert.Equal(0, bridge.Dialogs.ClickCalls);
		DialogReport report = Assert.Single(reports);
		Assert.Equal("vaultDownloadProgress", report.Type);
		Assert.Equal("waited on until it closes by itself", report.Action);
	}

	[Fact]
	public async Task ProgressDialogNeedsAPersonAfterTheLimit()
	{
		await using TestBridge bridge = new();
		(Task<string> call, _, _) = await bridge.StartCallAsync();
		bridge.Dialogs.Dialogs = [_downloadProgress];

		await bridge.AdvanceAsync(BridgeClient.FirstDialogCheck);
		bridge.Time.Advance(BridgeClient.ProgressDialogWaitLimit);
		await bridge.AdvanceAsync(BridgeClient.DialogCheckInterval * 2);

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(() => call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
		Assert.Equal(BridgeErrorCodes.BlockedByDialog, exception.Code);
		Assert.Equal(0, bridge.Dialogs.ClickCalls);
	}

	[Fact]
	public async Task NothingIsSentUntilAProgressDialogCloses()
	{
		await using TestBridge bridge = new();
		Task<TestConnection> accept = bridge.Server.AcceptAsync();
		Assert.True(await bridge.Client.TryConnectAsync(TestContext.Current.CancellationToken));
		TestConnection connection = await accept;
		bridge.Dialogs.Dialogs = [_downloadProgress];

		Task<string> call = bridge.Client.InvokeAsync<string>(BridgeOperations.Session, null, TestContext.Current.CancellationToken);
		await bridge.AdvanceAsync(BridgeClient.DialogCheckInterval * 2);
		await TestBridge.WaitUntilAsync(() => bridge.Dialogs.DetectCalls >= 2);
		Assert.Equal(0, connection.QueuedRequests);
		Assert.False(call.IsCompleted);

		bridge.Dialogs.Dialogs = null;
		await bridge.AdvanceAsync(BridgeClient.DialogCheckInterval * 2);
		TestRequest request = await connection.ReadRequestAsync();
		await connection.RespondAsync(request.Id, "done");

		Assert.Equal("done", await call);
		Assert.Equal(0, bridge.Dialogs.ClickCalls);
	}

	[Fact]
	public async Task BlockWithNoDialogStopsTheWaitOnTheSecondCheck()
	{
		await using TestBridge bridge = new();
		(Task<string> call, _, _) = await bridge.StartCallAsync();
		bridge.Dialogs.BlockedWithoutDialog = true;

		await bridge.AdvanceAsync(BridgeClient.FirstDialogCheck + TimeSpan.FromMilliseconds(250));
		// The first call is the check before the send.
		await TestBridge.WaitUntilAsync(() => bridge.Dialogs.DetectCalls == 2);
		Assert.False(call.IsCompleted);

		await bridge.AdvanceAsync(BridgeClient.DialogCheckInterval);

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(() => call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
		Assert.Equal(BridgeErrorCodes.BlockedByDialog, exception.Code);
	}
}
