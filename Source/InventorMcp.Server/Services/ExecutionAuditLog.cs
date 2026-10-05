using System.Globalization;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	Writes the server's entries to <c>&lt;year&gt;\executed-code.log</c>, which the add-in of that release writes for each snippet.
/// </summary>
internal static class ExecutionAuditLog
{
	/// <summary>
	/// 	The root of the logs. Each release has its own folder below it, ex. <c>2026</c>.
	/// </summary>
	/// <remarks>
	/// 	Settable so that a test writes its fake dialog clicks to a temporary folder, not to the user's audit trail.
	/// </remarks>
	public static string LogRoot { get; set; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"InventorMcp");

	/// <summary>
	/// 	Records a click on a dialog button, with the dialog text.
	/// </summary>
	/// <param name="dialog">
	/// 	The dialog.
	/// </param>
	/// <param name="buttonName">
	/// 	The button that was clicked.
	/// </param>
	/// <param name="clickedBy">
	/// 	What clicked (ex. <c>watchdog</c> or <c>inventor_dialog_click</c>).
	/// </param>
	/// <param name="releaseYear">
	/// 	The release of the Inventor with the dialog, which picks the folder. Null writes to <see cref="LogRoot"/>.
	/// </param>
	/// <exception cref="IOException">
	/// 	The log could not be written.
	/// </exception>
	public static void WriteDialogClick(DialogSnapshot dialog, string buttonName, string clickedBy, int? releaseYear)
	{
		string newLine = Environment.NewLine;
		string entry =
			$"{new string('=', 80)}{newLine}" +
			$"{DateTimeOffset.UtcNow:O}  dialog-click  by={OneLine(clickedBy)}  button='{OneLine(buttonName)}'  title='{OneLine(dialog.Title)}'{newLine}" +
			$"{new string('-', 80)}{newLine}" +
			$"{dialog.Text}{newLine}";

		string directory = releaseYear is int year ? Path.Combine(LogRoot, year.ToString(CultureInfo.InvariantCulture)) : LogRoot;

		_ = Directory.CreateDirectory(directory);
		File.AppendAllText(Path.Combine(directory, "executed-code.log"), entry);
	}

	/// <summary>
	/// 	Makes a value safe for a header line of the log.
	/// </summary>
	/// <remarks>
	/// 	The value can come from outside the server (ex. a dialog title, or the client name in <c>initialize</c>).
	/// 	A line break in it could write a line that looks like a new entry.
	/// </remarks>
	/// <param name="value">
	/// 	The value.
	/// </param>
	/// <returns>
	/// 	The value with each control character replaced by a space.
	/// </returns>
	public static string OneLine(string value) =>
		string.Concat(value.Select(static character => char.IsControl(character) ? ' ' : character));
}