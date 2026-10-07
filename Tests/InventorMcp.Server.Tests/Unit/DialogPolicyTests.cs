using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The server clicks only on a dialog type of the catalog, with the button that the settings give, and only when the
/// 	dialog has the buttons of the example that was read.
/// </summary>
[Trait("Level", "Unit")]
public sealed class DialogPolicyTests
{
	private const string _iLogicTitle = "Error on line 16 in rule: Drawing_Main, in document: Frame Shop Drawing.idw";
	private const string _winFormsClass = "WindowsForms10.Window.8.app.0.22c9f37_r3_ad1";
	private const string _compileErrorTitle = "Rule Compile Errors in McpSecurityTest, in Part1";
	private const string _trustThisRule = "Assume that this external rule is safe";
	private const string _trustTheFolder = "Assume that all external rules in this folder are safe";

	private const string _securityAlertText =
		"Security Alert\r\n---\r\niLogic has disabled a potentially harmful rule. (external rule \"McpSecurityTest\" running from " +
		"file \"Part1\")\r\n\r\nIf you trust the contents of this rule and would like to enable it on your machine, click Run the rule.";

	private static readonly string _defaultText = DefaultText();
	private static readonly DialogSettings _defaults = DialogSettings.Defaults;
	private static readonly DialogSettings _trustsRules = TestDialogSettings.TrustsRules;

	private const string _unhandledExceptionText =
		"Unhandled exception has occurred in a component in your application. If you click Continue, the application will " +
		"ignore this error and attempt to continue.\r\n\r\n";

	private const string _disposedObjectText =
		_unhandledExceptionText + "Cannot access a disposed object.\r\nObject name: 'DevExpress.XtraTab.XtraTabPage'.";

	[Fact]
	public void ILogicErrorWithOnlyOkCloses() =>
		Assert.True(ShouldClose(Dialog(_iLogicTitle, _winFormsClass, Button("OK")), autoCloseEnabled: true));

	[Fact]
	public void MessageBoxWithOnlyOkCloses() =>
		Assert.True(ShouldClose(Dialog("Autodesk Inventor", "#32770", Button("OK")), autoCloseEnabled: true));

	[Fact]
	public void HiddenButtonsDoNotCount() =>
		Assert.True(ShouldClose(
			Dialog(_iLogicTitle, _winFormsClass, Button("OK"), Button("Cancel", visible: false), Button("Apply", visible: false)),
			autoCloseEnabled: true));

	[Fact]
	public void TitleBarButtonsDoNotCount() =>
		Assert.True(ShouldClose(
			Dialog("Autodesk Inventor", "#32770", Button("OK"), new DialogButton("Close", true, true, IsWindowFrame: true)),
			autoCloseEnabled: true));

	[Fact]
	public void ScrollBarButtonsDoNotCount() =>
		Assert.True(ShouldClose(
			Dialog(_iLogicTitle, _winFormsClass, Button("OK"), new DialogButton("Line up", true, true, IsWindowFrame: true), new DialogButton("Column right", true, true, IsWindowFrame: true)),
			autoCloseEnabled: true));

	[Fact]
	public void DisabledButtonsDoNotCount() =>
		Assert.True(ShouldClose(
			Dialog("Autodesk Inventor", "#32770", Button("OK"), Button("Retry", enabled: false)),
			autoCloseEnabled: true));

	[Fact]
	public void QuestionStaysOpen() =>
		Assert.False(ShouldClose(Dialog("Autodesk Inventor", "#32770", Button("Yes"), Button("No")), autoCloseEnabled: true));

	[Fact]
	public void OkAndCancelStaysOpen() =>
		Assert.False(ShouldClose(Dialog("Autodesk Inventor", "#32770", Button("OK"), Button("Cancel")), autoCloseEnabled: true));

	[Fact]
	public void UnknownTypeWithOnlyOkStaysOpen() =>
		Assert.False(ShouldClose(Dialog("Custom Tool Message", _winFormsClass, Button("OK")), autoCloseEnabled: true));

	[Fact]
	public void SettingOffLeavesEveryDialogOpen() =>
		Assert.False(ShouldClose(Dialog(_iLogicTitle, _winFormsClass, Button("OK")), autoCloseEnabled: false));

	[Fact]
	public void UnreadDialogStaysOpen() =>
		Assert.False(ShouldClose(
			Dialog("Autodesk Inventor", "#32770", Button("OK")) with { ReadError = "The dialog did not answer within 2 s." },
			autoCloseEnabled: true));

	[Fact]
	public void OkMustMatchExactly() =>
		Assert.False(ShouldClose(Dialog("Autodesk Inventor", "#32770", Button("OK to all")), autoCloseEnabled: true));

	[Fact]
	public void MigrationDialogClosesWhenAccepted() =>
		Assert.True(ShouldClose(MigrationDialog(Button("OK"), Button("Cancel"), Button("Help")), autoCloseEnabled: true, acceptMigrationEnabled: true));

	[Fact]
	public void MigrationDialogWithoutHelpClosesWhenAccepted() =>
		Assert.True(ShouldClose(MigrationDialog(Button("OK"), Button("Cancel")), autoCloseEnabled: true, acceptMigrationEnabled: true));

	[Fact]
	public void MigrationDialogStaysOpenByDefault() =>
		Assert.False(ShouldClose(MigrationDialog(Button("OK"), Button("Cancel"), Button("Help")), autoCloseEnabled: true));

	[Fact]
	public void MigrationDialogStaysOpenWhenAutoCloseIsOff() =>
		Assert.False(ShouldClose(MigrationDialog(Button("OK"), Button("Cancel"), Button("Help")), autoCloseEnabled: false, acceptMigrationEnabled: true));

	[Fact]
	public void MigrationDialogWithoutCancelStaysOpen() =>
		Assert.False(ShouldClose(MigrationDialog(Button("OK"), Button("Help")), autoCloseEnabled: true, acceptMigrationEnabled: true));

	[Fact]
	public void MigrationDialogWithAnUnknownButtonStaysOpen() =>
		Assert.False(ShouldClose(MigrationDialog(Button("OK"), Button("Cancel"), Button("Help"), Button("Do not ask again")), autoCloseEnabled: true, acceptMigrationEnabled: true));

	[Fact]
	public void MigrationDialogWithNoButtonsReadStaysOpen() =>
		Assert.False(ShouldClose(MigrationDialog(), autoCloseEnabled: true, acceptMigrationEnabled: true));

	[Fact]
	public void UnreadMigrationDialogStaysOpen() =>
		Assert.False(ShouldClose(
			MigrationDialog(Button("OK"), Button("Cancel")) with { ReadError = "The dialog did not answer within 2 s." },
			autoCloseEnabled: true,
			acceptMigrationEnabled: true));

	[Fact]
	public void MigrationTitleOfADifferentClassIsNotTheMigrationDialog() =>
		Assert.False(ShouldClose(
			Dialog("Data Format Has Changed", _winFormsClass, Button("OK"), Button("Cancel")),
			autoCloseEnabled: true,
			acceptMigrationEnabled: true));

	[Fact]
	public void AcceptingMigrationDoesNotCloseOtherQuestions() =>
		Assert.False(ShouldClose(Dialog("Autodesk Inventor", "#32770", Button("OK"), Button("Cancel")), autoCloseEnabled: true, acceptMigrationEnabled: true));

	[Fact]
	public void DisposedObjectErrorClosesWithContinue()
	{
		DialogSnapshot dialog = DotNetErrorDialog(_disposedObjectText, Button("Details"), Button("Continue"));

		Assert.True(ShouldClose(dialog, autoCloseEnabled: true));
		Assert.Equal("Continue", DialogPolicy.ButtonToClick(dialog, _defaults));
	}

	[Fact]
	public void DisposedObjectErrorWithoutDetailsCloses() =>
		Assert.True(ShouldClose(DotNetErrorDialog(_disposedObjectText, Button("Continue")), autoCloseEnabled: true));

	[Fact]
	public void DisposedObjectErrorStaysOpenWhenAutoCloseIsOff() =>
		Assert.False(ShouldClose(DotNetErrorDialog(_disposedObjectText, Button("Details"), Button("Continue")), autoCloseEnabled: false));

	[Fact]
	public void DotNetErrorOfADifferentExceptionStaysOpen() =>
		Assert.False(ShouldClose(
			DotNetErrorDialog(_unhandledExceptionText + "Object reference not set to an instance of an object.", Button("Details"), Button("Continue")),
			autoCloseEnabled: true));

	[Fact]
	public void DotNetErrorWithQuitStaysOpen() =>
		Assert.False(ShouldClose(DotNetErrorDialog(_disposedObjectText, Button("Details"), Button("Continue"), Button("Quit")), autoCloseEnabled: true));

	[Fact]
	public void DotNetErrorWithoutContinueStaysOpen() =>
		Assert.False(ShouldClose(DotNetErrorDialog(_disposedObjectText, Button("Details")), autoCloseEnabled: true));

	[Fact]
	public void UnreadDotNetErrorStaysOpen() =>
		Assert.False(ShouldClose(
			DotNetErrorDialog(_disposedObjectText, Button("Details"), Button("Continue")) with { ReadError = "The dialog did not answer within 2 s." },
			autoCloseEnabled: true));

	[Fact]
	public void OtherTypesCloseWithOk() =>
		Assert.Equal("OK", DialogPolicy.ButtonToClick(Dialog(_iLogicTitle, _winFormsClass, Button("OK")), _defaults));

	[Fact]
	public void CompileErrorWithOnlyOkCloses() =>
		Assert.True(ShouldClose(
			Dialog(_compileErrorTitle, _winFormsClass, Button("OK"), Button("", visible: false), Button("Apply", visible: false), Button("Cancel", visible: false)),
			autoCloseEnabled: true));

	[Fact]
	public void SecurityAlertStaysOpenByDefault()
	{
		DialogSnapshot dialog = SecurityAlert(Button("Don't run the rule"), Button("Run the rule"), Button("Show details"));

		Assert.Equal("iLogicSecurityAlert", DialogPolicy.Classify(dialog));
		Assert.Null(DialogPolicy.ButtonToClick(dialog, _defaults));
	}

	[Fact]
	public void SecurityAlertCanBeSetToRunTheRule() =>
		Assert.Equal(
			"Run the rule",
			DialogPolicy.ButtonToClick(SecurityAlert(Button("Don't run the rule"), Button("Run the rule"), Button("Show details")), _trustsRules));

	[Fact]
	public void SecurityAlertCanBeSetToNotRunTheRule() =>
		Assert.Equal(
			"Don't run the rule",
			DialogPolicy.ButtonToClick(
				SecurityAlert(Button("Don't run the rule"), Button("Run the rule"), Button("Show details")),
				WithUserFile("""{ "iLogicSecurityAlert": "Don't run the rule" }""")));

	[Fact]
	public void SecurityAlertAskStaysOpen() =>
		Assert.Null(DialogPolicy.ButtonToClick(
			SecurityAlert(Button("Don't run the rule"), Button("Run the rule")),
			WithUserFile("""{ "iLogicSecurityAlert": "Ask" }""")));

	[Fact]
	public void SecurityAlertWithAnUnknownButtonStaysOpen() =>
		Assert.Null(DialogPolicy.ButtonToClick(SecurityAlert(Button("Don't run the rule"), Button("Run the rule"), Button("Always run")), _trustsRules));

	[Fact]
	public void SecurityAlertWithoutItsMessageIsAMessageBox() =>
		Assert.Equal("messageBox", DialogPolicy.Classify(new DialogSnapshot(0x1234, "Security Alert", "#32770", "Win32", "A certificate expired.", [Button("OK")])));

	[Fact]
	public void SecurityAlertStaysOpenWhenAutoCloseIsOff() =>
		Assert.False(ShouldClose(SecurityAlert(Button("Don't run the rule"), Button("Run the rule")), autoCloseEnabled: false));

	[Fact]
	public void AdvisorStaysOpenByDefault() =>
		Assert.Null(DialogPolicy.ButtonToClick(Advisor(Option(_trustThisRule, true), Option(_trustTheFolder, false)), _defaults));

	[Fact]
	public void AdvisorTrustingTheRuleClosesWithOk() =>
		Assert.Equal("OK", DialogPolicy.ButtonToClick(Advisor(Option(_trustThisRule, true), Option(_trustTheFolder, false)), _trustsRules));

	[Fact]
	public void AdvisorTrustingTheFolderStaysOpen() =>
		Assert.Null(DialogPolicy.ButtonToClick(Advisor(Option(_trustThisRule, false), Option(_trustTheFolder, true)), _trustsRules));

	[Fact]
	public void AdvisorWithNoOptionReadStaysOpen() =>
		Assert.Null(DialogPolicy.ButtonToClick(Advisor(), _trustsRules));

	[Fact]
	public void AdvisorAskStaysOpen() =>
		Assert.Null(DialogPolicy.ButtonToClick(
			Advisor(Option(_trustThisRule, true), Option(_trustTheFolder, false)),
			WithUserFile("""{ "iLogicSecurityAdvisor": "Ask" }""")));

	[Fact]
	public void ConsequenceOfEachClickIsReported()
	{
		Assert.Contains("this release's format", DialogPolicy.ClickConsequence("migration", "OK"));
		Assert.Contains("runs the rule", DialogPolicy.ClickConsequence("iLogicSecurityAlert", "Run the rule"));
		Assert.Contains("disabled the rule", DialogPolicy.ClickConsequence("iLogicSecurityAlert", "Don't run the rule"));
		Assert.Contains("from now on", DialogPolicy.ClickConsequence("iLogicSecurityAdvisor", "OK"));
		Assert.Equal(string.Empty, DialogPolicy.ClickConsequence("iLogicError", "OK"));
	}

	[Theory]
	[InlineData(_iLogicTitle, _winFormsClass, "iLogicError")]
	[InlineData("Data Format Has Changed", "#32770", "migration")]
	[InlineData("Data Format Has Changed", _winFormsClass, null)]
	[InlineData("Microsoft .NET", _winFormsClass, "dotNetDisposedObjectError")]
	[InlineData("Microsoft .NET", "#32770", "messageBox")]
	[InlineData("Autodesk Inventor", "#32770", "messageBox")]
	[InlineData("iLogic Security Alert", _winFormsClass, null)]
	[InlineData(_compileErrorTitle, _winFormsClass, "iLogicCompileError")]
	[InlineData(_compileErrorTitle, "#32770", "messageBox")]
	[InlineData("iLogic Security Advisor", _winFormsClass, "iLogicSecurityAdvisor")]
	[InlineData("Security Alert", "#32770", "messageBox")]
	[InlineData("Error in rule", _winFormsClass, null)]
	public void ClassifiesFromTheCatalog(string title, string className, string? expected) =>
		Assert.Equal(expected, DialogPolicy.Classify(Dialog(title, className)));

	/// <summary>
	/// 	The embedded defaults with the two environment switches: <paramref name="autoCloseEnabled"/> false makes every type
	/// 	"Ask", and <paramref name="acceptMigrationEnabled"/> sets the migration dialog to OK.
	/// </summary>
	private static bool ShouldClose(DialogSnapshot dialog, bool autoCloseEnabled, bool acceptMigrationEnabled = false) =>
		DialogPolicy.ShouldClose(dialog, DialogSettings.Create(
			_defaultText,
			userText: null,
			autoCloseValue: autoCloseEnabled ? null : "false",
			acceptMigrationValue: acceptMigrationEnabled ? "true" : null));

	private static DialogSettings WithUserFile(string userText) =>
		DialogSettings.Create(_defaultText, userText, autoCloseValue: null, acceptMigrationValue: null);

	private static string DefaultText()
	{
		using Stream stream = typeof(DialogSettings).Assembly.GetManifestResourceStream("InventorMcp.Server.DialogSettings.json")!;
		using StreamReader reader = new(stream);

		return reader.ReadToEnd();
	}

	private static DialogSnapshot Dialog(string title, string className, params DialogButton[] buttons) =>
		new(0x1234, title, className, "WinForm", "Text", buttons);

	/// <summary>
	/// 	Inventor's migration dialog as the Win32 read returns it. See Docs/Research/Blocking-Dialog-Detection.md.
	/// </summary>
	private static DialogSnapshot MigrationDialog(params DialogButton[] buttons) =>
		new(0x1234, "Data Format Has Changed", "#32770", "Win32", "The data format of the following files was migrated to the current release.", buttons);

	/// <summary>
	/// 	The .NET error dialog as the server log recorded it. See Docs/Research/Blocking-Dialog-Detection.md.
	/// </summary>
	private static DialogSnapshot DotNetErrorDialog(string text, params DialogButton[] buttons) =>
		new(0x1234, "Microsoft .NET", "WindowsForms10.Window.8.app.0.ffc8c_r3_ad1", "WinForm", text, buttons);

	/// <summary>
	/// 	The iLogic Security Alert as the server read it on Inventor 2027. See Docs/Research/Blocking-Dialog-Detection.md.
	/// </summary>
	private static DialogSnapshot SecurityAlert(params DialogButton[] buttons) =>
		new(0x1234, "Security Alert", "#32770", "Win32", _securityAlertText, buttons);

	/// <summary>
	/// 	The iLogic Security Advisor as the server read it on Inventor 2027, with the options the read missed then.
	/// </summary>
	private static DialogSnapshot Advisor(params DialogOption[] options) => new(
		0x1234,
		"iLogic Security Advisor",
		_winFormsClass,
		"WinForm",
		"In the future:\r\n---\r\nTo change  these options later:",
		[Button("Security Options"), Button("OK"), Button("<< Back"), Button("")],
		Choices: options);

	private static DialogOption Option(string name, bool selected) => new(name, "radio button", selected);

	private static DialogButton Button(string name, bool visible = true, bool enabled = true) => new(name, visible, enabled);
}