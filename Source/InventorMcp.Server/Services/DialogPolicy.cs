using System.Text.RegularExpressions;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	Decides which dialogs the server may close without a person, and with which button.
/// </summary>
/// <remarks>
/// 	A click is a user decision, so the server clicks only on a dialog type of the catalog, with the button that
/// 	<c>DialogSettings.jsonc</c> sets for that type, and only when the dialog has exactly the buttons of the example that
/// 	was read. Any other button makes it a dialog the server has not read, so it stays open.
/// 	Add a dialog type to the catalog only after a real example was read, and add its entry to
/// 	<c>DialogSettings.jsonc</c>.
/// 	See <c>Docs/Research/Blocking-Dialog-Detection.md</c>.
/// </remarks>
internal static partial class DialogPolicy
{
	/// <summary>
	/// 	The environment variable that turns the automatic close off with the value <c>false</c>.
	/// </summary>
	public const string AutoCloseVariable = "INVENTORMCP_AUTOCLOSE_DIALOGS";

	/// <summary>
	/// 	The environment variable that lets the server click OK on the migration dialog, with the value <c>true</c>.
	/// </summary>
	public const string AcceptMigrationVariable = "INVENTORMCP_ACCEPT_MIGRATION_DIALOG";

	/// <summary>
	/// 	The iLogic error dialog, after a rule throws.
	/// </summary>
	public const string ILogicErrorType = "iLogicError";

	/// <summary>
	/// 	The iLogic dialog for a rule that does not compile.
	/// </summary>
	public const string ILogicCompileErrorType = "iLogicCompileError";

	/// <summary>
	/// 	A Win32 message box.
	/// </summary>
	public const string MessageBoxType = "messageBox";

	/// <summary>
	/// 	The .NET dialog for an unhandled exception in a Windows Forms component, for a disposed object.
	/// </summary>
	public const string DotNetErrorType = "dotNetDisposedObjectError";

	/// <summary>
	/// 	Inventor's migration dialog.
	/// </summary>
	public const string MigrationType = "migration";

	/// <summary>
	/// 	The iLogic Security Alert for a rule that iLogic detects to be potentially unsafe.
	/// </summary>
	public const string SecurityAlertType = "iLogicSecurityAlert";

	/// <summary>
	/// 	The iLogic Security Advisor, which comes after "Run the rule" in the Security Alert.
	/// </summary>
	public const string SecurityAdvisorType = "iLogicSecurityAdvisor";

	/// <summary>
	/// 	The OK button.
	/// </summary>
	public const string OkButton = "OK";

	/// <summary>
	/// 	The button of the .NET error dialog that ignores the exception.
	/// </summary>
	public const string ContinueButton = "Continue";

	/// <summary>
	/// 	The button of the Security Alert that runs the rule.
	/// </summary>
	public const string RunRuleButton = "Run the rule";

	/// <summary>
	/// 	The button of the Security Alert that disables the rule.
	/// </summary>
	public const string DontRunRuleButton = "Don't run the rule";

	private const string _win32DialogClass = "#32770";
	private const string _winFormsClassPrefix = "WindowsForms10.";
	private const string _migrationTitle = "Data Format Has Changed";
	private const string _migrationCancelButton = "Cancel";
	private const string _migrationHelpButton = "Help";
	private const string _dotNetErrorTitle = "Microsoft .NET";
	private const string _dotNetErrorDetailsButton = "Details";
	private const string _disposedObjectMessage = "Cannot access a disposed object.";
	private const string _securityAlertTitle = "Security Alert";
	private const string _securityAlertMessage = "iLogic has disabled a potentially harmful rule.";
	private const string _securityAdvisorTitle = "iLogic Security Advisor";
	private const string _trustThisRuleOption = "Assume that this external rule is safe";

	private static readonly string[] _securityAlertDetailsButtons = ["Show details", "Hide details"];

	/// <summary>
	/// 	The Advisor's buttons apart from OK. The unnamed one is the help icon.
	/// </summary>
	private static readonly string[] _securityAdvisorOtherButtons = ["Security Options", "<< Back", ""];

	/// <summary>
	/// 	The buttons that <c>DialogSettings.jsonc</c> may name for each dialog type, apart from "Ask".
	/// </summary>
	public static IReadOnlyDictionary<string, IReadOnlyList<string>> Choices { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
	{
		[ILogicErrorType] = [OkButton],
		[ILogicCompileErrorType] = [OkButton],
		[MessageBoxType] = [OkButton],
		[DotNetErrorType] = [ContinueButton],
		[MigrationType] = [OkButton],
		[SecurityAlertType] = [RunRuleButton, DontRunRuleButton],
		[SecurityAdvisorType] = [OkButton]
	};

	/// <summary>
	/// 	Names the dialog type from the catalog.
	/// </summary>
	/// <param name="dialog">
	/// 	The dialog.
	/// </param>
	/// <returns>
	/// 	A key of <see cref="Choices"/>, the same as the entry in <c>DialogSettings.jsonc</c>, or null for a type that is
	/// 	not in the catalog.
	/// </returns>
	public static string? Classify(DialogSnapshot dialog)
	{
		bool isWinForms = dialog.ClassName.StartsWith(_winFormsClassPrefix, StringComparison.Ordinal);
		bool isWin32 = dialog.ClassName == _win32DialogClass;

		if (ILogicErrorTitle().IsMatch(dialog.Title))
			return ILogicErrorType;

		if (isWinForms && ILogicCompileErrorTitle().IsMatch(dialog.Title))
			return ILogicCompileErrorType;

		if (isWinForms && dialog.Title == _securityAdvisorTitle)
			return SecurityAdvisorType;

		if (isWinForms && dialog.Title == _dotNetErrorTitle)
			return DotNetErrorType;

		if (isWin32 && dialog.Title == _migrationTitle)
			return MigrationType;

		// The Security Alert is a task dialog, which has the class of a message box.
		if (isWin32 && dialog.Title == _securityAlertTitle && dialog.Text.Contains(_securityAlertMessage, StringComparison.Ordinal))
			return SecurityAlertType;

		if (isWin32)
			return MessageBoxType;

		return null;
	}

	/// <summary>
	/// 	Names the button that the server clicks on the dialog.
	/// </summary>
	/// <param name="dialog">
	/// 	The dialog.
	/// </param>
	/// <param name="settings">
	/// 	The settings, normally <see cref="DialogSettings.Load"/>.
	/// </param>
	/// <returns>
	/// 	The button, or null to leave the dialog for a person.
	/// </returns>
	public static string? ButtonToClick(DialogSnapshot dialog, DialogSettings settings)
	{
		if (dialog.ReadError is not null || Classify(dialog) is not string type || settings.ButtonFor(type) is not string button)
			return null;

		IReadOnlyList<string> buttons = dialog.ClickableButtons;

		bool asRead = type switch
		{
			MigrationType => HasOnly(buttons, [OkButton, _migrationCancelButton], [_migrationHelpButton]),
			DotNetErrorType => dialog.Text.Contains(_disposedObjectMessage, StringComparison.Ordinal)
				&& HasOnly(buttons, [ContinueButton], [_dotNetErrorDetailsButton]),
			SecurityAlertType => HasOnly(buttons, [RunRuleButton, DontRunRuleButton], _securityAlertDetailsButtons),
			SecurityAdvisorType => HasOnly(buttons, [OkButton], _securityAdvisorOtherButtons) && TrustsOnlyThisRule(dialog.Options),
			_ => buttons is [OkButton]
		};

		return asRead ? button : null;
	}

	/// <summary>
	/// 	True when the server clicks a button of the dialog.
	/// </summary>
	public static bool ShouldClose(DialogSnapshot dialog, DialogSettings settings) => ButtonToClick(dialog, settings) is not null;

	/// <summary>
	/// 	What a click changed, for the tool result, or an empty text when it only closed the dialog.
	/// </summary>
	/// <param name="type">
	/// 	The catalog type.
	/// </param>
	/// <param name="button">
	/// 	The button that the server clicked.
	/// </param>
	public static string ClickConsequence(string? type, string button) => (type, button) switch
	{
		(MigrationType, _) => ", so the files were saved in this release's format",
		(SecurityAlertType, RunRuleButton) => ", so iLogic runs the rule",
		(SecurityAlertType, DontRunRuleButton) =>
			", so iLogic disabled the rule until a person enables it in Tools > Options > iLogic Configuration",
		(SecurityAdvisorType, _) => ", so iLogic trusts this rule on this machine from now on",
		_ => string.Empty
	};

	/// <summary>
	/// 	True when the buttons hold each required button, and nothing else apart from the allowed ones.
	/// </summary>
	private static bool HasOnly(IReadOnlyList<string> buttons, string[] required, string[] allowed) =>
		required.All(buttons.Contains)
		&& buttons.All(button => required.Contains(button) || allowed.Contains(button));

	/// <summary>
	/// 	True when the selected option of the Advisor trusts only the one rule.
	/// </summary>
	/// <remarks>
	/// 	The other option trusts every external rule in the folder, so OK must not confirm it.
	/// 	With no option read, the scope is not known, so the dialog stays open.
	/// </remarks>
	private static bool TrustsOnlyThisRule(IReadOnlyList<DialogOption> options) =>
		options.Where(option => option.IsSelected).Select(option => option.Name).SequenceEqual([_trustThisRuleOption]);

	[GeneratedRegex(@"^Error on line \d+ in rule: ", RegexOptions.CultureInvariant)]
	private static partial Regex ILogicErrorTitle();

	[GeneratedRegex(@"^Rule Compile Errors in .+, in ", RegexOptions.CultureInvariant)]
	private static partial Regex ILogicCompileErrorTitle();
}
