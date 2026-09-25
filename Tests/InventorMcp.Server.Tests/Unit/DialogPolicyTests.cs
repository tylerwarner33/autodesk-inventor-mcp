using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The server closes only a known information dialog with one clickable button, named OK, and the migration dialog when
/// 	the user turned that on.
/// </summary>
[Trait("Level", "Unit")]
public sealed class DialogPolicyTests
{
	private const string _iLogicTitle = "Error on line 16 in rule: Drawing_Main, in document: Frame Shop Drawing.idw";
	private const string _winFormsClass = "WindowsForms10.Window.8.app.0.22c9f37_r3_ad1";

	[Fact]
	public void ILogicErrorWithOnlyOkCloses() =>
		Assert.True(DialogPolicy.ShouldClose(Dialog(_iLogicTitle, _winFormsClass, Button("OK")), autoCloseEnabled: true));

	[Fact]
	public void MessageBoxWithOnlyOkCloses() =>
		Assert.True(DialogPolicy.ShouldClose(Dialog("Autodesk Inventor", "#32770", Button("OK")), autoCloseEnabled: true));

	[Fact]
	public void HiddenButtonsDoNotCount() =>
		Assert.True(DialogPolicy.ShouldClose(
			Dialog(_iLogicTitle, _winFormsClass, Button("OK"), Button("Cancel", visible: false), Button("Apply", visible: false)),
			autoCloseEnabled: true));

	[Fact]
	public void TitleBarButtonsDoNotCount() =>
		Assert.True(DialogPolicy.ShouldClose(
			Dialog("Autodesk Inventor", "#32770", Button("OK"), new DialogButton("Close", true, true, IsWindowFrame: true)),
			autoCloseEnabled: true));

	[Fact]
	public void ScrollBarButtonsDoNotCount() =>
		Assert.True(DialogPolicy.ShouldClose(
			Dialog(_iLogicTitle, _winFormsClass, Button("OK"), new DialogButton("Line up", true, true, IsWindowFrame: true), new DialogButton("Column right", true, true, IsWindowFrame: true)),
			autoCloseEnabled: true));

	[Fact]
	public void DisabledButtonsDoNotCount() =>
		Assert.True(DialogPolicy.ShouldClose(
			Dialog("Autodesk Inventor", "#32770", Button("OK"), Button("Retry", enabled: false)),
			autoCloseEnabled: true));

	[Fact]
	public void QuestionStaysOpen() =>
		Assert.False(DialogPolicy.ShouldClose(Dialog("Autodesk Inventor", "#32770", Button("Yes"), Button("No")), autoCloseEnabled: true));

	[Fact]
	public void OkAndCancelStaysOpen() =>
		Assert.False(DialogPolicy.ShouldClose(Dialog("Autodesk Inventor", "#32770", Button("OK"), Button("Cancel")), autoCloseEnabled: true));

	[Fact]
	public void UnknownTypeWithOnlyOkStaysOpen() =>
		Assert.False(DialogPolicy.ShouldClose(Dialog("Custom Tool Message", _winFormsClass, Button("OK")), autoCloseEnabled: true));

	[Fact]
	public void SettingOffLeavesEveryDialogOpen() =>
		Assert.False(DialogPolicy.ShouldClose(Dialog(_iLogicTitle, _winFormsClass, Button("OK")), autoCloseEnabled: false));

	[Fact]
	public void UnreadDialogStaysOpen() =>
		Assert.False(DialogPolicy.ShouldClose(
			Dialog("Autodesk Inventor", "#32770", Button("OK")) with { ReadError = "The dialog did not answer within 2 s." },
			autoCloseEnabled: true));

	[Fact]
	public void OkMustMatchExactly() =>
		Assert.False(DialogPolicy.ShouldClose(Dialog("Autodesk Inventor", "#32770", Button("OK to all")), autoCloseEnabled: true));

	[Fact]
	public void MigrationDialogClosesWhenAccepted() =>
		Assert.True(DialogPolicy.ShouldClose(MigrationDialog(Button("OK"), Button("Cancel"), Button("Help")), autoCloseEnabled: true, acceptMigrationEnabled: true));

	[Fact]
	public void MigrationDialogWithoutHelpClosesWhenAccepted() =>
		Assert.True(DialogPolicy.ShouldClose(MigrationDialog(Button("OK"), Button("Cancel")), autoCloseEnabled: true, acceptMigrationEnabled: true));

	[Fact]
	public void MigrationDialogStaysOpenByDefault() =>
		Assert.False(DialogPolicy.ShouldClose(MigrationDialog(Button("OK"), Button("Cancel"), Button("Help")), autoCloseEnabled: true));

	[Fact]
	public void MigrationDialogStaysOpenWhenAutoCloseIsOff() =>
		Assert.False(DialogPolicy.ShouldClose(MigrationDialog(Button("OK"), Button("Cancel"), Button("Help")), autoCloseEnabled: false, acceptMigrationEnabled: true));

	[Fact]
	public void MigrationDialogWithoutCancelStaysOpen() =>
		Assert.False(DialogPolicy.ShouldClose(MigrationDialog(Button("OK"), Button("Help")), autoCloseEnabled: true, acceptMigrationEnabled: true));

	[Fact]
	public void MigrationDialogWithAnUnknownButtonStaysOpen() =>
		Assert.False(DialogPolicy.ShouldClose(MigrationDialog(Button("OK"), Button("Cancel"), Button("Help"), Button("Do not ask again")), autoCloseEnabled: true, acceptMigrationEnabled: true));

	[Fact]
	public void MigrationDialogWithNoButtonsReadStaysOpen() =>
		Assert.False(DialogPolicy.ShouldClose(MigrationDialog(), autoCloseEnabled: true, acceptMigrationEnabled: true));

	[Fact]
	public void UnreadMigrationDialogStaysOpen() =>
		Assert.False(DialogPolicy.ShouldClose(
			MigrationDialog(Button("OK"), Button("Cancel")) with { ReadError = "The dialog did not answer within 2 s." },
			autoCloseEnabled: true,
			acceptMigrationEnabled: true));

	[Fact]
	public void MigrationTitleOfADifferentClassIsNotTheMigrationDialog() =>
		Assert.False(DialogPolicy.ShouldClose(
			Dialog("Data Format Has Changed", _winFormsClass, Button("OK"), Button("Cancel")),
			autoCloseEnabled: true,
			acceptMigrationEnabled: true));

	[Fact]
	public void AcceptingMigrationDoesNotCloseOtherQuestions() =>
		Assert.False(DialogPolicy.ShouldClose(Dialog("Autodesk Inventor", "#32770", Button("OK"), Button("Cancel")), autoCloseEnabled: true, acceptMigrationEnabled: true));

	[Theory]
	[InlineData(_iLogicTitle, _winFormsClass, "iLogic error")]
	[InlineData("Data Format Has Changed", "#32770", "migration")]
	[InlineData("Data Format Has Changed", _winFormsClass, null)]
	[InlineData("Autodesk Inventor", "#32770", "message box")]
	[InlineData("iLogic Security Alert", _winFormsClass, null)]
	[InlineData("Error in rule", _winFormsClass, null)]
	public void ClassifiesFromTheCatalog(string title, string className, string? expected) =>
		Assert.Equal(expected, DialogPolicy.Classify(Dialog(title, className)));

	private static DialogSnapshot Dialog(string title, string className, params DialogButton[] buttons) =>
		new(0x1234, title, className, "WinForm", "Text", buttons);

	/// <summary>
	/// 	Inventor's migration dialog as the Win32 read returns it. See Docs/Research/Blocking-Dialog-Detection.md.
	/// </summary>
	private static DialogSnapshot MigrationDialog(params DialogButton[] buttons) =>
		new(0x1234, "Data Format Has Changed", "#32770", "Win32", "The data format of the following files was migrated to the current release.", buttons);

	private static DialogButton Button(string name, bool visible = true, bool enabled = true) => new(name, visible, enabled);
}