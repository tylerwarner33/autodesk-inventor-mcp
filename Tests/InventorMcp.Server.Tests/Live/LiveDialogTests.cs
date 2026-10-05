using System.Diagnostics;

using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;
using InventorMcp.Server.Tests.Desktop;

namespace InventorMcp.Server.Tests.Live;

/// <summary>
/// 	The full path through a live Inventor, the same as a tool call.
/// </summary>
/// <remarks>
/// 	Needs Inventor with the add-in loaded. Each test creates and closes its own documents.
/// 	See <c>.agents/rules/build.md</c>, "Tests", and <c>Docs/Research/Blocking-Dialog-Detection.md</c>, "Live test results".
/// </remarks>
[Trait("Level", "Live")]
[Collection(DesktopCollection.Name)]
public sealed class LiveDialogTests
{
	private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

	[Fact]
	public async Task ILogicErrorThroughAutomationIsClosed()
	{
		LiveInventor.Require();
		await using BridgeClient client = LiveInventor.CreateClient();
		List<DialogReport> reports = DialogReports.Begin();
		string missingRule = LiveInventor.UniqueName("DoesNotExist");

		Stopwatch stopwatch = Stopwatch.StartNew();
		ExecutionResult result = await LiveInventor.EvalAsync(client, LiveInventor.ILogicErrorSnippet(LiveInventor.UniqueName("McpTest"), missingRule), Cancellation);
		stopwatch.Stop();

		// No report means that no dialog opened under SilentOperationScope. Record that in the research.
		DialogReport report = Assert.Single(reports);
		Assert.Equal("iLogicError", report.Type);
		Assert.Equal("closed with OK", report.Action);
		Assert.Contains(missingRule, report.Text);
		Assert.True(result.Succeeded, LiveInventor.Describe(result));
		TestContext.Current.SendDiagnosticMessage($"Closed after {stopwatch.Elapsed.TotalSeconds:0.0} s. Text: {report.Text}");
	}

	[Fact]
	public async Task ILogicErrorThroughTheRuleToolIsClosed()
	{
		LiveInventor.Require();
		await using BridgeClient client = LiveInventor.CreateClient();
		string missingRule = LiveInventor.UniqueName("DoesNotExist");

		ExecutionResult created = await LiveInventor.EvalAsync(client, """
			Document part = (Document)Application.Documents.Add(
				DocumentTypeEnum.kPartDocumentObject,
				Application.FileManager.GetTemplateFile(DocumentTypeEnum.kPartDocumentObject),
				true);
			return part.DisplayName;
			""", Cancellation);
		string documentName = created.ReturnValue!;

		try
		{
			List<DialogReport> reports = DialogReports.Begin();

			_ = await LiveInventor.RunILogicAsync(client, $"iLogicVb.RunExternalRule(\"{missingRule}\")", documentName, Cancellation);

			DialogReport report = Assert.Single(reports);
			Assert.Equal("closed with OK", report.Action);
			Assert.Contains(missingRule, report.Text);
		}
		finally
		{
			_ = await LiveInventor.EvalAsync(client, $"Application.Documents.ItemByName[\"{documentName}\"].Close(true); return null;", Cancellation);
		}
	}

	[Fact]
	public async Task MessageBoxWithOkIsClosed()
	{
		LiveInventor.Require();
		await using BridgeClient client = LiveInventor.CreateClient();
		List<DialogReport> reports = DialogReports.Begin();

		ExecutionResult result = await LiveInventor.EvalAsync(client, LiveInventor.MessageBoxSnippet("McpTest OK", "OK"), Cancellation);

		DialogReport report = Assert.Single(reports);
		Assert.Equal("messageBox", report.Type);
		Assert.Equal("closed with OK", report.Action);
		Assert.Contains("McpTest message text", report.Text);
		Assert.Equal("OK", result.ReturnValue);
	}

	[Fact]
	public async Task QuestionStaysOpenAndHealthReportsItAtOnce()
	{
		LiveInventor.Require();
		await using BridgeClient client = LiveInventor.CreateClient();
		await using BridgeClient health = LiveInventor.CreateClient();
		_ = await health.TryConnectAsync(Cancellation);

		Task<ExecutionResult> question = LiveInventor.EvalAsync(client, LiveInventor.MessageBoxSnippet("McpTest Question", "YesNo"), Cancellation);

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(() => question.WaitAsync(TimeSpan.FromSeconds(20), Cancellation));
		Assert.Equal(BridgeErrorCodes.BlockedByDialog, exception.Code);
		Assert.Contains("Visible buttons: Yes, No", exception.Message);

		// A second server reads the block with no bridge call, while the main thread is still held.
		Stopwatch stopwatch = Stopwatch.StartNew();
		BlockState? state = await health.GetBlockStateAsync(Cancellation);
		stopwatch.Stop();
		Assert.True(state?.Blocked, "The block state did not show the question.");
		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"The block state took {stopwatch.Elapsed}.");

		(int processId, DialogSnapshot dialog) = await LiveInventor.WaitForDialogAsync(health, Cancellation);
		Assert.Equal(ClickStatus.Clicked, new BlockingDialogs().TryClick(processId, dialog, "No").Status);

		// The late answer of the first call is still in the pipe. The next call must get its own.
		ExecutionResult next = await LiveInventor.EvalAsync(client, "return \"after the question\";", Cancellation);
		Assert.Equal("after the question", next.ReturnValue);
	}

	[Fact]
	public async Task TwoServersClickOneTime()
	{
		LiveInventor.Require();
		await using BridgeClient first = LiveInventor.CreateClient();
		await using BridgeClient second = LiveInventor.CreateClient();
		_ = await second.TryConnectAsync(Cancellation);

		List<DialogReport> firstReports = [];
		List<DialogReport> secondReports = [];

		Task<ExecutionResult> error = Task.Run(async () =>
		{
			firstReports = DialogReports.Begin();
			return await LiveInventor.EvalAsync(first, LiveInventor.ILogicErrorSnippet(LiveInventor.UniqueName("McpTest"), LiveInventor.UniqueName("DoesNotExist")), Cancellation);
		}, Cancellation);

		// The second call waits behind the dialog on the main thread, so its watchdog sees the same dialog.
		await Task.Delay(500, Cancellation);
		Task<SessionInfo> session = Task.Run(async () =>
		{
			secondReports = DialogReports.Begin();
			return await second.InvokeAsync<SessionInfo>(BridgeOperations.Session, null, Cancellation);
		}, Cancellation);

		_ = await error.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
		_ = await session.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);

		int clicks = firstReports.Concat(secondReports).Count(report => report.Action == "closed with OK");
		Assert.Equal(1, clicks);
	}

	[Fact]
	public async Task MigrationDialogIsClosedWhenAccepted()
	{
		LiveInventor.Require();
		string part = LiveInventor.CopyOldReleasePart();
		await using BridgeClient client = LiveInventor.CreateClient();
		string? previous = Environment.GetEnvironmentVariable(DialogPolicy.AcceptMigrationVariable);
		Environment.SetEnvironmentVariable(DialogPolicy.AcceptMigrationVariable, "true");

		try
		{
			List<DialogReport> reports = DialogReports.Begin();

			ExecutionResult result = await LiveInventor.EvalAsync(client, LiveInventor.SaveOldReleasePartSnippet(part), Cancellation);

			DialogReport report = Assert.Single(reports);
			Assert.Equal(DialogPolicy.MigrationType, report.Type);
			Assert.StartsWith("closed with OK", report.Action);
			Assert.Contains("migrated", report.Text);
			Assert.Contains("Cancel", report.Buttons);
			Assert.True(result.Succeeded, LiveInventor.Describe(result));
			TestContext.Current.SendDiagnosticMessage($"Saved by: {result.ReturnValue}. Buttons: {string.Join(", ", report.Buttons)}. Text: {report.Text}");
		}
		finally
		{
			Environment.SetEnvironmentVariable(DialogPolicy.AcceptMigrationVariable, previous);
			_ = await LiveInventor.EvalAsync(client, LiveInventor.CloseDocumentsUnderSnippet(Path.GetDirectoryName(part)!), Cancellation);
			Directory.Delete(Path.GetDirectoryName(part)!, recursive: true);
		}
	}

	[Fact]
	public async Task MigrationDialogStaysOpenByDefault()
	{
		LiveInventor.Require();
		string part = LiveInventor.CopyOldReleasePart();
		await using BridgeClient client = LiveInventor.CreateClient();
		string? previous = Environment.GetEnvironmentVariable(DialogPolicy.AcceptMigrationVariable);
		Environment.SetEnvironmentVariable(DialogPolicy.AcceptMigrationVariable, null);

		try
		{
			Task<ExecutionResult> save = LiveInventor.EvalAsync(client, LiveInventor.SaveOldReleasePartSnippet(part), Cancellation);

			InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(() => save.WaitAsync(TimeSpan.FromSeconds(20), Cancellation));
			Assert.Equal(BridgeErrorCodes.BlockedByDialog, exception.Code);
			Assert.Contains("Visible buttons: OK, Cancel", exception.Message);

			// Cancel keeps the copy in its earlier release's format, through the Win32 click.
			(int processId, DialogSnapshot dialog) = await LiveInventor.WaitForDialogAsync(client, Cancellation);
			Assert.Equal(ClickStatus.Clicked, new BlockingDialogs().TryClick(processId, dialog, "Cancel").Status);
			ExecutionResult next = await LiveInventor.EvalAsync(client, "return \"after the migration dialog\";", Cancellation);
			Assert.Equal("after the migration dialog", next.ReturnValue);
		}
		finally
		{
			Environment.SetEnvironmentVariable(DialogPolicy.AcceptMigrationVariable, previous);
			_ = await LiveInventor.EvalAsync(client, LiveInventor.CloseDocumentsUnderSnippet(Path.GetDirectoryName(part)!), Cancellation);
			Directory.Delete(Path.GetDirectoryName(part)!, recursive: true);
		}
	}

	[Fact]
	public async Task SettingOffClicksNothing()
	{
		LiveInventor.Require();
		await using BridgeClient client = LiveInventor.CreateClient();
		string? previous = Environment.GetEnvironmentVariable(DialogPolicy.AutoCloseVariable);
		Environment.SetEnvironmentVariable(DialogPolicy.AutoCloseVariable, "false");

		try
		{
			Task<ExecutionResult> error = LiveInventor.EvalAsync(client, LiveInventor.ILogicErrorSnippet(LiveInventor.UniqueName("McpTest"), LiveInventor.UniqueName("DoesNotExist")), Cancellation);

			InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(() => error.WaitAsync(TimeSpan.FromSeconds(20), Cancellation));
			Assert.Equal(BridgeErrorCodes.BlockedByDialog, exception.Code);
		}
		finally
		{
			Environment.SetEnvironmentVariable(DialogPolicy.AutoCloseVariable, previous);
		}

		// Close the dialog, so the snippet can close its part.
		(int processId, DialogSnapshot dialog) = await LiveInventor.WaitForDialogAsync(client, Cancellation);
		Assert.Equal(ClickStatus.Clicked, new BlockingDialogs().TryClick(processId, dialog, "OK").Status);
		ExecutionResult next = await LiveInventor.EvalAsync(client, "return \"after the error\";", Cancellation);
		Assert.Equal("after the error", next.ReturnValue);
	}
}