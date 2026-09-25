namespace InventorMcp.Server.Services;

/// <summary>
/// 	Whether a modal dialog blocks Inventor, and the dialogs that do it.
/// </summary>
/// <param name="ProcessId">
/// 	The Inventor process that was examined.
/// </param>
/// <param name="Blocked">
/// 	True when the main window is disabled, which a modal dialog does to its owner.
/// </param>
/// <param name="MainWindowFound">
/// 	False when the process has no visible main window, so the state is not known.
/// </param>
/// <param name="Dialogs">
/// 	The visible, enabled top level windows of the process other than the main window.
/// 	Empty when <paramref name="Blocked"/> is false, or when the blocking dialog belongs to a different process.
/// </param>
internal sealed record BlockState(int ProcessId, bool Blocked, bool MainWindowFound, IReadOnlyList<DialogSnapshot> Dialogs)
{
	/// <summary>
	/// 	The title of the first dialog, or a short reason when the block has no dialog of Inventor's process.
	/// </summary>
	public string? Summary => Blocked
		? Dialogs.Count > 0
			? Dialogs[0].Title
			: "a window of a different process (ex. Vault or a licence service), or a dialog with no title"
		: null;
}

/// <summary>
/// 	What the server read from one dialog.
/// </summary>
/// <param name="Handle">
/// 	The window handle.
/// </param>
/// <param name="Title">
/// 	The window title.
/// </param>
/// <param name="ClassName">
/// 	The Win32 window class (ex. <c>#32770</c> for a message box).
/// </param>
/// <param name="Framework">
/// 	The UI Automation framework (ex. <c>Win32</c>, <c>WinForm</c>, <c>WPF</c>), or null when the dialog was not read.
/// </param>
/// <param name="Text">
/// 	All text of the dialog, also of tabs that are not selected, with <c>---</c> between parts.
/// </param>
/// <param name="Buttons">
/// 	Every button that UI Automation found, with its state.
/// </param>
/// <param name="ReadError">
/// 	Why the dialog could not be read (ex. it did not answer in time), or null.
/// </param>
internal sealed record DialogSnapshot(
	long Handle,
	string Title,
	string ClassName,
	string? Framework,
	string Text,
	IReadOnlyList<DialogButton> Buttons,
	string? ReadError = null)
{
	/// <summary>
	/// 	The names of the buttons a user can click.
	/// </summary>
	public IReadOnlyList<string> ClickableButtons => [.. Buttons.Where(button => button.IsClickable).Select(button => button.Name)];
}

/// <summary>
/// 	One button of a dialog.
/// </summary>
/// <param name="Name">
/// 	The button text (ex. <c>OK</c>).
/// </param>
/// <param name="IsVisible">
/// 	True when the user can see it.
/// 	Dialogs can keep hidden template buttons (ex. the iLogic error dialog).
/// </param>
/// <param name="IsEnabled">
/// 	True when the button accepts a click.
/// </param>
/// <param name="IsInTitleBar">
/// 	True for the minimise, maximise and close buttons of the title bar.
/// </param>
internal sealed record DialogButton(string Name, bool IsVisible, bool IsEnabled, bool IsInTitleBar = false)
{
	/// <summary>
	/// 	True when a user can see and click the button in the dialog itself.
	/// </summary>
	public bool IsClickable => IsVisible && IsEnabled && IsInTitleBar is false;
}

/// <summary>
/// 	The result of a click on a dialog button.
/// </summary>
internal enum ClickStatus
{
	/// <summary>
	/// 	The button was clicked.
	/// </summary>
	Clicked,

	/// <summary>
	/// 	The dialog closed before the click, ex. a different server or the user closed it.
	/// </summary>
	DialogClosed,

	/// <summary>
	/// 	No click: zero or more than one visible, enabled button has the name, or the dialog changed.
	/// </summary>
	Refused,

	/// <summary>
	/// 	The dialog did not answer, or Windows refused the click.
	/// </summary>
	Failed
}

/// <summary>
/// 	The result of a click, with a message for the model.
/// </summary>
internal sealed record ClickOutcome(ClickStatus Status, string Message);