using System.Text.RegularExpressions;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	Decides which dialogs the server may close without a person.
/// </summary>
/// <remarks>
/// 	A click is a user decision, so the server closes only a dialog that asks nothing: a known information dialog with
/// 	one clickable button, named OK.
/// 	Add a dialog type to the catalog only after a real example was read.
/// 	See <c>Docs/Research/Blocking-Dialog-Detection.md</c>, "Proposal for the server".
/// </remarks>
internal static partial class DialogPolicy
{
	/// <summary>
	/// 	The environment variable that turns the automatic close off with the value <c>false</c>.
	/// </summary>
	public const string AutoCloseVariable = "INVENTORMCP_AUTOCLOSE_DIALOGS";

	/// <summary>
	/// 	The only button the server clicks.
	/// </summary>
	public const string CloseButton = "OK";

	/// <summary>
	/// 	True unless the user set <see cref="AutoCloseVariable"/> to <c>false</c>.
	/// </summary>
	public static bool IsAutoCloseEnabled => string.Equals(
		Environment.GetEnvironmentVariable(AutoCloseVariable)?.Trim(),
		"false",
		StringComparison.OrdinalIgnoreCase) is false;

	/// <summary>
	/// 	Names the dialog type from the catalog.
	/// </summary>
	/// <param name="dialog">
	/// 	The dialog.
	/// </param>
	/// <returns>
	/// 	<c>iLogic error</c>, <c>message box</c>, or null for a type that is not in the catalog.
	/// </returns>
	public static string? Classify(DialogSnapshot dialog)
	{
		if (ILogicErrorTitle().IsMatch(dialog.Title))
			return "iLogic error";

		if (dialog.ClassName == "#32770")
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
	/// <returns>
	/// 	True to click <see cref="CloseButton"/>.
	/// </returns>
	public static bool ShouldClose(DialogSnapshot dialog, bool autoCloseEnabled) =>
		autoCloseEnabled
		&& dialog.ReadError is null
		&& Classify(dialog) is not null
		&& dialog.ClickableButtons is [CloseButton];

	[GeneratedRegex(@"^Error on line \d+ in rule: ", RegexOptions.CultureInvariant)]
	private static partial Regex ILogicErrorTitle();
}