using System.Text.RegularExpressions;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	Decides which dialogs the server may close without a person.
/// </summary>
/// <remarks>
/// 	A click is a user decision, so the server closes only a dialog that asks nothing: a known information dialog with
/// 	one clickable button, named OK.
/// 	The one exception is Inventor's migration dialog, which asks whether to save files in the new format. The server
/// 	clicks its OK only when the user turned that on with <see cref="AcceptMigrationVariable"/>.
/// 	The .NET error dialog for a disposed object is closed with <see cref="ContinueButton"/>, because its only other
/// 	choice ends Inventor.
/// 	Add a dialog type to the catalog only after a real example was read.
/// 	See <c>Docs/Research/Blocking-Dialog-Detection.md</c>, "Proposal for the server", "The migration dialog" and
/// 	"The .NET error dialog".
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
	/// 	The button the server clicks on every dialog type except the .NET error dialog.
	/// </summary>
	public const string CloseButton = "OK";

	/// <summary>
	/// 	The button the server clicks on the .NET error dialog.
	/// </summary>
	public const string ContinueButton = "Continue";

	/// <summary>
	/// 	The catalog type of Inventor's migration dialog.
	/// </summary>
	public const string MigrationType = "migration";

	/// <summary>
	/// 	The catalog type of the .NET dialog for an unhandled exception in a Windows Forms component.
	/// </summary>
	public const string DotNetErrorType = ".NET error";

	private const string _migrationTitle = "Data Format Has Changed";
	private const string _win32DialogClass = "#32770";
	private const string _migrationCancelButton = "Cancel";
	private const string _migrationHelpButton = "Help";
	private const string _dotNetErrorTitle = "Microsoft .NET";
	private const string _winFormsClassPrefix = "WindowsForms10.";
	private const string _dotNetErrorDetailsButton = "Details";
	private const string _disposedObjectMessage = "Cannot access a disposed object.";

	/// <summary>
	/// 	True unless the user set <see cref="AutoCloseVariable"/> to <c>false</c>.
	/// </summary>
	public static bool IsAutoCloseEnabled => string.Equals(
		Environment.GetEnvironmentVariable(AutoCloseVariable)?.Trim(),
		"false",
		StringComparison.OrdinalIgnoreCase) is false;

	/// <summary>
	/// 	True only when the user set <see cref="AcceptMigrationVariable"/> to <c>true</c>.
	/// </summary>
	public static bool IsAcceptMigrationEnabled => string.Equals(
		Environment.GetEnvironmentVariable(AcceptMigrationVariable)?.Trim(),
		"true",
		StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// 	Names the dialog type from the catalog.
	/// </summary>
	/// <param name="dialog">
	/// 	The dialog.
	/// </param>
	/// <returns>
	/// 	<c>iLogic error</c>, <c>migration</c>, <c>.NET error</c>, <c>message box</c>, or null for a type that is not in
	/// 	the catalog.
	/// </returns>
	public static string? Classify(DialogSnapshot dialog)
	{
		if (ILogicErrorTitle().IsMatch(dialog.Title))
			return "iLogic error";

		if (dialog.ClassName == _win32DialogClass && dialog.Title == _migrationTitle)
			return MigrationType;

		if (dialog.ClassName.StartsWith(_winFormsClassPrefix, StringComparison.Ordinal) && dialog.Title == _dotNetErrorTitle)
			return DotNetErrorType;

		if (dialog.ClassName == _win32DialogClass)
			return "message box";

		return null;
	}

	/// <summary>
	/// 	Decides whether the server closes the dialog.
	/// </summary>
	/// <param name="dialog">
	/// 	The dialog.
	/// </param>
	/// <param name="autoCloseEnabled">
	/// 	The setting, normally <see cref="IsAutoCloseEnabled"/>.
	/// </param>
	/// <param name="acceptMigrationEnabled">
	/// 	The migration setting, normally <see cref="IsAcceptMigrationEnabled"/>.
	/// 	It has an effect only when <paramref name="autoCloseEnabled"/> is true too.
	/// </param>
	/// <returns>
	/// 	True to click the button that <see cref="ButtonToClick"/> names.
	/// </returns>
	public static bool ShouldClose(DialogSnapshot dialog, bool autoCloseEnabled, bool acceptMigrationEnabled = false)
	{
		if (autoCloseEnabled is false || dialog.ReadError is not null)
			return false;

		return Classify(dialog) switch
		{
			null => false,
			MigrationType => acceptMigrationEnabled && HasMigrationButtons(dialog.ClickableButtons),
			DotNetErrorType => dialog.Text.Contains(_disposedObjectMessage, StringComparison.Ordinal)
				&& HasDotNetErrorButtons(dialog.ClickableButtons),
			_ => dialog.ClickableButtons is [CloseButton]
		};
	}

	/// <summary>
	/// 	Names the button that closes the dialog when <see cref="ShouldClose"/> is true.
	/// </summary>
	/// <param name="dialog">
	/// 	The dialog.
	/// </param>
	/// <returns>
	/// 	<see cref="ContinueButton"/> for the .NET error dialog, and <see cref="CloseButton"/> for all other types.
	/// </returns>
	public static string ButtonToClick(DialogSnapshot dialog) =>
		Classify(dialog) == DotNetErrorType ? ContinueButton : CloseButton;

	/// <summary>
	/// 	True when the buttons are exactly OK and Cancel, with Help allowed beside them.
	/// </summary>
	/// <remarks>
	/// 	Any other button (ex. a check box that a later release adds) makes it a dialog the server has not read, so it
	/// 	stays open.
	/// </remarks>
	private static bool HasMigrationButtons(IReadOnlyList<string> buttons)
	{
		List<string> decisions = [.. buttons.Where(button => button != _migrationHelpButton)];

		return decisions.Count == 2
			&& decisions.Contains(CloseButton)
			&& decisions.Contains(_migrationCancelButton);
	}

	/// <summary>
	/// 	True when the only button, apart from Details, is Continue.
	/// </summary>
	/// <remarks>
	/// 	Details only shows the stack trace.
	/// 	A dialog with Quit, or any other button, stays open, because the example that was read had none.
	/// </remarks>
	private static bool HasDotNetErrorButtons(IReadOnlyList<string> buttons) =>
		buttons.Where(button => button != _dotNetErrorDetailsButton).SequenceEqual([ContinueButton]);

	[GeneratedRegex(@"^Error on line \d+ in rule: ", RegexOptions.CultureInvariant)]
	private static partial Regex ILogicErrorTitle();
}
