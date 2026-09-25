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
/// 	Add a dialog type to the catalog only after a real example was read.
/// 	See <c>Docs/Research/Blocking-Dialog-Detection.md</c>, "Proposal for the server" and "The migration dialog".
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
	/// 	The only button the server clicks.
	/// </summary>
	public const string CloseButton = "OK";

	/// <summary>
	/// 	The catalog type of Inventor's migration dialog.
	/// </summary>
	public const string MigrationType = "migration";

	private const string _migrationTitle = "Data Format Has Changed";
	private const string _win32DialogClass = "#32770";
	private const string _migrationCancelButton = "Cancel";
	private const string _migrationHelpButton = "Help";

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
	/// 	<c>iLogic error</c>, <c>migration</c>, <c>message box</c>, or null for a type that is not in the catalog.
	/// </returns>
	public static string? Classify(DialogSnapshot dialog)
	{
		if (ILogicErrorTitle().IsMatch(dialog.Title))
			return "iLogic error";

		if (dialog.ClassName == _win32DialogClass && dialog.Title == _migrationTitle)
			return MigrationType;

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
	/// 	True to click <see cref="CloseButton"/>.
	/// </returns>
	public static bool ShouldClose(DialogSnapshot dialog, bool autoCloseEnabled, bool acceptMigrationEnabled = false)
	{
		if (autoCloseEnabled is false || dialog.ReadError is not null)
			return false;

		return Classify(dialog) switch
		{
			null => false,
			MigrationType => acceptMigrationEnabled && HasMigrationButtons(dialog.ClickableButtons),
			_ => dialog.ClickableButtons is [CloseButton]
		};
	}

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

	[GeneratedRegex(@"^Error on line \d+ in rule: ", RegexOptions.CultureInvariant)]
	private static partial Regex ILogicErrorTitle();
}
