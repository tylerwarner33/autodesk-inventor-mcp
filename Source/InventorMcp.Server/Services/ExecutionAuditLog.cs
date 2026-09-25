namespace InventorMcp.Server.Services;

/// <summary>
/// 	Writes the server's entries to <c>executed-code.log</c>, which the add-in writes for each snippet.
/// </summary>
internal static class ExecutionAuditLog
{
	private static readonly string _path = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"InventorMcp",
		"executed-code.log");

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
	/// <exception cref="IOException">
	/// 	The log could not be written.
	/// </exception>
	public static void WriteDialogClick(DialogSnapshot dialog, string buttonName, string clickedBy)
	{
		string newLine = Environment.NewLine;
		string entry =
			$"{new string('=', 80)}{newLine}" +
			$"{DateTimeOffset.UtcNow:O}  dialog-click  by={OneLine(clickedBy)}  button='{OneLine(buttonName)}'  title='{OneLine(dialog.Title)}'{newLine}" +
			$"{new string('-', 80)}{newLine}" +
			$"{dialog.Text}{newLine}";

		_ = Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
		File.AppendAllText(_path, entry);
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