namespace InventorMcp.Server.Services;

/// <summary>
/// 	A dialog that the server found while a tool call waited.
/// </summary>
/// <param name="Title">
/// 	The window title.
/// </param>
/// <param name="Type">
/// 	The type from the catalog (ex. <c>iLogic error</c>), or null for a type that is not in it.
/// </param>
/// <param name="Text">
/// 	All text of the dialog.
/// </param>
/// <param name="Buttons">
/// 	The buttons a user can click.
/// </param>
/// <param name="Action">
/// 	What the server did (ex. <c>closed with OK</c>).
/// </param>
internal sealed record DialogReport(string Title, string? Type, string Text, IReadOnlyList<string> Buttons, string Action);

/// <summary>
/// 	Collects the dialogs of one tool call, so the tool result can name them.
/// </summary>
/// <remarks>
/// 	The bridge call runs below the tool in the same asynchronous flow, so an <see cref="AsyncLocal{T}"/> carries the list
/// 	down to the watchdog without a change to each tool.
/// </remarks>
internal static class DialogReports
{
	private static readonly AsyncLocal<List<DialogReport>?> _current = new();

	/// <summary>
	/// 	Starts a new list for the current tool call.
	/// </summary>
	/// <returns>
	/// 	The list, which the watchdog fills.
	/// </returns>
	public static List<DialogReport> Begin()
	{
		List<DialogReport> reports = [];
		_current.Value = reports;

		return reports;
	}

	/// <summary>
	/// 	Adds a dialog to the list of the current tool call, if there is one.
	/// </summary>
	/// <param name="report">
	/// 	The dialog.
	/// </param>
	public static void Add(DialogReport report) => _current.Value?.Add(report);
}