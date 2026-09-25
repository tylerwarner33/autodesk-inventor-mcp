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
			$"{DateTimeOffset.UtcNow:O}  dialog-click  by={clickedBy}  button='{buttonName}'  title='{dialog.Title}'{newLine}" +
			$"{new string('-', 80)}{newLine}" +
			$"{dialog.Text}{newLine}";

		_ = Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
		File.AppendAllText(_path, entry);
	}
}