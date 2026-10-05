using System.Diagnostics;

using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests.Desktop;

/// <summary>
/// 	Detection, reading and clicks against real dialogs in a separate process, with no Inventor.
/// </summary>
[Trait("Level", "Desktop")]
[Collection(DesktopCollection.Name)]
public sealed class BlockingDialogsTests
{
	private static readonly TimeSpan _exitTimeout = TimeSpan.FromSeconds(5);

	private readonly BlockingDialogs _detector = new(DialogFixture.IsMainWindow);

	[Fact]
	public async Task NoDialogIsNotBlocked()
	{
		using DialogFixture fixture = await DialogFixture.StartAsync("none");

		BlockState state = _detector.Detect(fixture.ProcessId);

		Assert.True(state.MainWindowFound);
		Assert.False(state.Blocked);
		Assert.Empty(state.Dialogs);
	}

	[Fact]
	public async Task MessageBoxIsDetectedAndRead()
	{
		using DialogFixture fixture = await DialogFixture.StartAsync("ok");

		BlockState state = _detector.Detect(fixture.ProcessId);

		Assert.True(state.Blocked);
		DialogSnapshot dialog = Assert.Single(state.Dialogs);
		Assert.Equal(fixture.DialogHandle, dialog.Handle);
		Assert.Equal("Test OK", dialog.Title);
		Assert.Equal("#32770", dialog.ClassName);
		Assert.Contains("Test message", dialog.Text);
		Assert.Equal(["OK"], dialog.ClickableButtons);
		Assert.Contains(dialog.Buttons, button => button.IsWindowFrame);
		Assert.Equal("messageBox", DialogPolicy.Classify(dialog));
		Assert.True(DialogPolicy.ShouldClose(dialog, DialogSettings.Defaults));
	}

	[Fact]
	public async Task ILogicLikeDialogHasOnlyOk()
	{
		using DialogFixture fixture = await DialogFixture.StartAsync("ilogic-like");

		DialogSnapshot dialog = Assert.Single(_detector.Detect(fixture.ProcessId).Dialogs);

		// A WinForms tab that is not selected has no window until it opens, so only the live test can check the
		// second tab of the real DevExpress dialog. See Docs/Research/Blocking-Dialog-Detection.md.
		Assert.Equal(["OK"], dialog.ClickableButtons);
		Assert.Contains(dialog.Buttons, button => button.IsWindowFrame && button.Name == "Line down");
		Assert.Contains("Cannot find an external rule file", dialog.Text);
		Assert.Equal("iLogicError", DialogPolicy.Classify(dialog));
		Assert.True(DialogPolicy.ShouldClose(dialog, DialogSettings.Defaults));
	}

	[Fact]
	public async Task WpfDialogIsRead()
	{
		using DialogFixture fixture = await DialogFixture.StartAsync("wpf");

		DialogSnapshot dialog = Assert.Single(_detector.Detect(fixture.ProcessId).Dialogs);

		Assert.Equal("WPF", dialog.Framework);
		Assert.Contains("WPF message text", dialog.Text);
		Assert.Equal(["OK"], dialog.ClickableButtons);

		// Not in the catalog, so it stays open.
		Assert.False(DialogPolicy.ShouldClose(dialog, DialogSettings.Defaults));
	}

	[Fact]
	public async Task ClickOkClosesTheDialog()
	{
		using DialogFixture fixture = await DialogFixture.StartAsync("ok");
		DialogSnapshot dialog = Assert.Single(_detector.Detect(fixture.ProcessId).Dialogs);

		ClickOutcome outcome = _detector.TryClick(fixture.ProcessId, dialog, "OK");

		Assert.Equal(ClickStatus.Clicked, outcome.Status);
		Assert.True(fixture.WaitForExit(_exitTimeout), "The fixture did not exit, so the dialog did not close.");
	}

	[Theory]
	[InlineData("yesno", new[] { "Yes", "No" })]
	[InlineData("okcancel", new[] { "OK", "Cancel" })]
	[InlineData("unknown-ok", new[] { "OK" })]
	public async Task DialogThatNeedsAPersonStaysOpen(string mode, string[] buttons)
	{
		using DialogFixture fixture = await DialogFixture.StartAsync(mode);

		DialogSnapshot dialog = Assert.Single(_detector.Detect(fixture.ProcessId).Dialogs);

		Assert.Equal(buttons, dialog.ClickableButtons);
		Assert.False(DialogPolicy.ShouldClose(dialog, DialogSettings.Defaults));
		Assert.False(fixture.HasExited);
	}

	[Fact]
	public async Task AdvisorOptionsAreReadAndOnlyTheOneRuleIsTrusted()
	{
		using DialogFixture fixture = await DialogFixture.StartAsync("advisor");
		DialogSnapshot dialog = Assert.Single(_detector.Detect(fixture.ProcessId).Dialogs);

		Assert.Equal("iLogicSecurityAdvisor", DialogPolicy.Classify(dialog));
		Assert.Equal(
			[new DialogOption("Assume that this external rule is safe", "radio button", true), new DialogOption("Assume that all external rules in this folder are safe", "radio button", false)],
			dialog.Options);
		Assert.Equal("OK", DialogPolicy.ButtonToClick(dialog, DialogSettings.Defaults));
	}

	[Fact]
	public async Task AdvisorThatTrustsTheFolderStaysOpen()
	{
		using DialogFixture fixture = await DialogFixture.StartAsync("advisor-folder");
		DialogSnapshot dialog = Assert.Single(_detector.Detect(fixture.ProcessId).Dialogs);

		Assert.Contains(dialog.Options, option => option.Name.Contains("all external rules", StringComparison.Ordinal) && option.IsSelected);
		Assert.Null(DialogPolicy.ButtonToClick(dialog, DialogSettings.Defaults));
		Assert.False(fixture.HasExited);
	}

	[Fact]
	public async Task HiddenButtonIsNotClicked()
	{
		using DialogFixture fixture = await DialogFixture.StartAsync("ilogic-like");
		DialogSnapshot dialog = Assert.Single(_detector.Detect(fixture.ProcessId).Dialogs);

		ClickOutcome outcome = _detector.TryClick(fixture.ProcessId, dialog, "Cancel");

		Assert.Equal(ClickStatus.Refused, outcome.Status);
		Assert.False(fixture.HasExited);
	}

	[Fact]
	public async Task DialogThatClosesBeforeTheClickIsReported()
	{
		using DialogFixture fixture = await DialogFixture.StartAsync("ok", "--close-after", "200");
		DialogSnapshot dialog = Assert.Single(_detector.Detect(fixture.ProcessId).Dialogs);

		await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
		ClickOutcome outcome = _detector.TryClick(fixture.ProcessId, dialog, "OK");

		Assert.Equal(ClickStatus.DialogClosed, outcome.Status);
	}

	[Fact]
	public async Task DialogThatDoesNotAnswerStopsTheReadAtTheTimeLimit()
	{
		using DialogFixture fixture = await DialogFixture.StartAsync("hang");

		// The fixture UI thread starts to sleep after it wrote READY. Let it start.
		await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

		Stopwatch stopwatch = Stopwatch.StartNew();
		BlockState state = _detector.Detect(fixture.ProcessId);
		stopwatch.Stop();

		Assert.True(state.Blocked);
		DialogSnapshot dialog = Assert.Single(state.Dialogs);
		Assert.NotNull(dialog.ReadError);
		Assert.False(DialogPolicy.ShouldClose(dialog, DialogSettings.Defaults));
		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"The read took {stopwatch.Elapsed}.");
	}

	/// <summary>
	/// 	The Win32 read that a dialog with no UI Automation buttons falls back to, on a real Win32 message box.
	/// </summary>
	/// <remarks>
	/// 	The fixture has no dialog that hides its buttons from UI Automation, as Inventor's migration dialog does, so the
	/// 	fallback is called directly. See Docs/Research/Blocking-Dialog-Detection.md, "The migration dialog".
	/// </remarks>
	[Fact]
	public async Task Win32ReadFindsTheButtonsAndTheirIds()
	{
		using DialogFixture fixture = await DialogFixture.StartAsync("okcancel");

		IReadOnlyList<DialogButton> buttons = BlockingDialogs.ReadWin32Buttons(fixture.DialogHandle);

		Assert.Contains(buttons, button => button is { Name: "OK", ControlId: 1, IsClickable: true });
		Assert.Contains(buttons, button => button is { Name: "Cancel", ControlId: 2, IsClickable: true });
	}

	[Fact]
	public async Task Win32ClickClosesTheDialogWithTheChosenButton()
	{
		using DialogFixture fixture = await DialogFixture.StartAsync("okcancel");
		DialogSnapshot dialog = Assert.Single(_detector.Detect(fixture.ProcessId).Dialogs);

		ClickOutcome outcome = _detector.TryClickWin32(fixture.ProcessId, dialog, "Cancel");

		Assert.Equal(ClickStatus.Clicked, outcome.Status);
		Assert.True(fixture.WaitForExit(_exitTimeout), "The fixture did not exit, so the dialog did not close.");
	}

	[Fact]
	public async Task Win32ClickOfAMissingButtonIsRefused()
	{
		using DialogFixture fixture = await DialogFixture.StartAsync("okcancel");
		DialogSnapshot dialog = Assert.Single(_detector.Detect(fixture.ProcessId).Dialogs);

		ClickOutcome outcome = _detector.TryClickWin32(fixture.ProcessId, dialog, "Retry");

		Assert.Equal(ClickStatus.Refused, outcome.Status);
		Assert.False(fixture.HasExited);
	}

	[Fact]
	public async Task OnlyOneServerClicks()
	{
		using DialogFixture fixture = await DialogFixture.StartAsync("ok");
		DialogSnapshot dialog = Assert.Single(_detector.Detect(fixture.ProcessId).Dialogs);
		BlockingDialogs second = new(DialogFixture.IsMainWindow);

		ClickOutcome[] outcomes = await Task.WhenAll(
			Task.Run(() => _detector.TryClick(fixture.ProcessId, dialog, "OK"), TestContext.Current.CancellationToken),
			Task.Run(() => second.TryClick(fixture.ProcessId, dialog, "OK"), TestContext.Current.CancellationToken));

		_ = Assert.Single(outcomes, outcome => outcome.Status is ClickStatus.Clicked);
		_ = Assert.Single(outcomes, outcome => outcome.Status is ClickStatus.DialogClosed);
	}
}