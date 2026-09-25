using System.Runtime.InteropServices;
using System.Text;

using Interop.UIAutomationClient;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	Finds, reads and clicks the dialogs that block a process.
/// </summary>
internal interface IBlockingDialogs
{
	/// <summary>
	/// 	Examines the process for a modal dialog.
	/// </summary>
	/// <param name="processId">
	/// 	The Inventor process.
	/// </param>
	/// <returns>
	/// 	The block state, with each dialog read.
	/// </returns>
	BlockState Detect(int processId);

	/// <summary>
	/// 	Clicks one visible, enabled button of a dialog that <see cref="Detect"/> returned.
	/// </summary>
	/// <param name="processId">
	/// 	The process that owns the dialog.
	/// </param>
	/// <param name="dialog">
	/// 	The dialog.
	/// </param>
	/// <param name="buttonName">
	/// 	The button text (ex. <c>OK</c>).
	/// </param>
	/// <returns>
	/// 	What happened.
	/// </returns>
	ClickOutcome TryClick(int processId, DialogSnapshot dialog, string buttonName);
}

/// <summary>
/// 	Finds a modal dialog of Inventor from outside its process, reads it, and clicks it.
/// </summary>
/// <remarks>
/// 	A modal dialog holds Inventor's main thread, so no bridge call can reply while it is open.
/// 	Win32 finds the windows, because a UI Automation search from the desktop root can miss an owned WinForms dialog.
/// 	UI Automation reads the text and the buttons of each window, in all three of Inventor's UI frameworks.
/// 	A Win32 dialog in which UI Automation finds no button is read, and clicked, through its Win32 child windows.
/// 	See <c>Docs/Research/Blocking-Dialog-Detection.md</c>.
/// </remarks>
/// <param name="isMainWindow">
/// 	Tests a window class and title for the main window, or null for the Inventor main frame.
/// </param>
/// <param name="readTimeout">
/// 	The time limit for each read and each click, or null for 2 s.
/// </param>
internal sealed class BlockingDialogs(Func<string, string, bool>? isMainWindow = null, TimeSpan? readTimeout = null) : IBlockingDialogs
{
	private const string _textSeparator = "\r\n---\r\n";
	private const string _win32DialogClass = "#32770";
	private const uint _getAncestorRoot = 2;
	private const uint _commandMessage = 0x0111;
	private const uint _sendAbortIfHung = 0x0002;

	private static readonly TimeSpan _closeWait = TimeSpan.FromSeconds(1);

	private readonly Func<string, string, bool> _isMainWindow = isMainWindow ?? IsInventorMainFrame;
	private readonly TimeSpan _readTimeout = readTimeout ?? TimeSpan.FromSeconds(2);
	private readonly Lazy<IUIAutomation> _automation = new(() => CreateAutomation(readTimeout ?? TimeSpan.FromSeconds(2)));

	/// <summary>
	/// 	The Inventor main frame is an MFC window, ex. <c>AfxMDIFrame140u</c>.
	/// </summary>
	public static bool IsInventorMainFrame(string className, string title) =>
		className.StartsWith("AfxMDIFrame", StringComparison.Ordinal);

	public BlockState Detect(int processId)
	{
		List<WindowInfo> windows = VisibleTopLevelWindows(processId);
		WindowInfo? main = windows.FirstOrDefault(window => _isMainWindow(window.ClassName, window.Title));

		if (main is null)
			return new BlockState(processId, Blocked: false, MainWindowFound: false, []);

		// A modal dialog disables its owner, so a disabled main window is the block signal.
		if (main.Enabled)
			return new BlockState(processId, Blocked: false, MainWindowFound: true, []);

		List<DialogSnapshot> dialogs = [.. windows
			.Where(window => window != main && window.Enabled && window.Title.Length > 0)
			.Select(ReadWithTimeout)];

		return new BlockState(processId, Blocked: true, MainWindowFound: true, dialogs);
	}

	public ClickOutcome TryClick(int processId, DialogSnapshot dialog, string buttonName) =>
		TryClick(processId, dialog, buttonName, win32Only: false);

	/// <summary>
	/// 	Clicks a button through its Win32 control id only, with no UI Automation.
	/// </summary>
	/// <remarks>
	/// 	For the desktop tests, which have no dialog that hides its buttons from UI Automation.
	/// </remarks>
	internal ClickOutcome TryClickWin32(int processId, DialogSnapshot dialog, string buttonName) =>
		TryClick(processId, dialog, buttonName, win32Only: true);

	private ClickOutcome TryClick(int processId, DialogSnapshot dialog, string buttonName, bool win32Only)
	{
		// Up to 16 servers can wait on the same dialog. Only the one that holds the mutex clicks.
		using Mutex mutex = new(false, $@"Local\InventorMcp.Dialogs.{processId}");

		bool owned;

		try
		{
			owned = mutex.WaitOne(_readTimeout + _closeWait);
		}
		catch (AbandonedMutexException)
		{
			owned = true;
		}

		if (owned is false)
			return new ClickOutcome(ClickStatus.Failed, "A different server is clicking a dialog of this Inventor. Examine the state again.");

		try
		{
			return ClickWhileOwned(dialog, buttonName, win32Only);
		}
		finally
		{
			mutex.ReleaseMutex();
		}
	}

	private ClickOutcome ClickWhileOwned(DialogSnapshot dialog, string buttonName, bool win32Only)
	{
		IntPtr handle = new(dialog.Handle);

		if (IsOpen(handle) is false)
			return DialogClosedOutcome(dialog);

		// A handle can be reused by a new window, so the title must still match what the caller saw.
		string title = GetTitle(handle);

		if (title != dialog.Title)
			return new ClickOutcome(ClickStatus.Refused, $"The window is now '{title}', not '{dialog.Title}'. Examine the state again.");

		try
		{
			ClickOutcome outcome = WithTimeout(() => win32Only
				? InvokeThroughWin32(handle, dialog.Title, buttonName)
				: Invoke(handle, dialog.Title, buttonName));

			if (outcome.Status is ClickStatus.Clicked)
				WaitForClose(handle);

			return outcome;
		}
		catch (TimeoutException)
		{
			return new ClickOutcome(ClickStatus.Failed, $"'{dialog.Title}' did not answer within {_readTimeout.TotalSeconds:0} s.");
		}
		catch (COMException) when (IsOpen(handle) is false)
		{
			return DialogClosedOutcome(dialog);
		}
		catch (COMException exception)
		{
			return new ClickOutcome(ClickStatus.Failed, $"Windows refused the click on '{buttonName}': {exception.Message}");
		}
	}

	private ClickOutcome Invoke(IntPtr handle, string title, string buttonName)
	{
		DialogContent content = Collect(handle);
		List<IUIAutomationElement> matches = [.. content.Buttons
			.Where(pair => pair.Button.IsClickable && pair.Button.Name == buttonName)
			.Select(pair => pair.Element)];

		// The same condition as the Win32 read in Read, so a button that the read found through Win32 is clicked that way.
		if (content.Buttons.Count == 0 && GetClassName(handle) == _win32DialogClass)
			return InvokeThroughWin32(handle, title, buttonName);

		if (matches.Count != 1)
			return new ClickOutcome(ClickStatus.Refused, $"'{title}' has {matches.Count} visible, enabled buttons named '{buttonName}', so nothing was clicked.");

		if (matches[0].GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) is not IUIAutomationInvokePattern invoke)
			return new ClickOutcome(ClickStatus.Failed, $"The button '{buttonName}' cannot be clicked through UI Automation.");

		invoke.Invoke();

		return new ClickOutcome(ClickStatus.Clicked, $"Clicked '{buttonName}' in '{title}'.");
	}

	/// <summary>
	/// 	Clicks a button of a Win32 dialog through the command message the button itself sends.
	/// </summary>
	/// <remarks>
	/// 	<c>BM_CLICK</c> from a different process can be lost when the dialog is not in front, so the dialog gets
	/// 	<c>WM_COMMAND</c> with the button's control id. The time limit stops a wait on a dialog that does not answer.
	/// 	See <c>Docs/Research/Blocking-Dialog-Detection.md</c>, "The migration dialog".
	/// </remarks>
	private ClickOutcome InvokeThroughWin32(IntPtr handle, string title, string buttonName)
	{
		List<Win32Control> matches = [.. Win32Controls(handle)
			.Where(control => control.IsButton && control.Visible && control.Enabled && control.Name == buttonName)];

		if (matches.Count != 1)
			return new ClickOutcome(ClickStatus.Refused, $"'{title}' has {matches.Count} visible, enabled buttons named '{buttonName}', so nothing was clicked.");

		// BN_CLICKED is 0, so the high word of wParam stays 0.
		IntPtr command = new(matches[0].Id & 0xFFFF);
		IntPtr answered = SendMessageTimeout(handle, _commandMessage, command, matches[0].Handle, _sendAbortIfHung, (uint)_readTimeout.TotalMilliseconds, out _);

		if (answered == IntPtr.Zero)
			return new ClickOutcome(ClickStatus.Failed, $"'{title}' did not answer the click on '{buttonName}' within {_readTimeout.TotalSeconds:0} s.");

		return new ClickOutcome(ClickStatus.Clicked, $"Clicked '{buttonName}' in '{title}' through its Win32 command.");
	}

	/// <summary>
	/// 	Reads the Win32 child windows of a dialog.
	/// </summary>
	/// <remarks>
	/// 	For a window of a different process, GetWindowText reads the stored text and sends no message, the same as the
	/// 	title read in <see cref="GetTitle"/>.
	/// </remarks>
	private static List<Win32Control> Win32Controls(IntPtr dialog) =>
		[.. ChildWindows(dialog).Select(child => new Win32Control(
			child,
			GetDlgCtrlID(child),
			GetClassName(child),
			GetTitle(child),
			IsWindowVisible(child),
			IsWindowEnabled(child)))];

	private DialogSnapshot ReadWithTimeout(WindowInfo window)
	{
		try
		{
			return WithTimeout(() => Read(window));
		}
		catch (TimeoutException)
		{
			return Unread(window, $"The dialog did not answer within {_readTimeout.TotalSeconds:0} s.");
		}
		catch (COMException exception)
		{
			return Unread(window, IsOpen(window.Handle) ? $"UI Automation failed: {exception.Message}" : "The dialog closed during the read.");
		}
	}

	private DialogSnapshot Read(WindowInfo window)
	{
		DialogContent content = Collect(window.Handle);

		// A WPF button holds its caption as a text element, which is not dialog text.
		List<string> texts = [.. content.Texts.Where(text => content.Buttons.Any(pair => pair.Button.Name == text) is false)];
		List<DialogButton> buttons = [.. content.Buttons.Select(pair => pair.Button)];

		// UI Automation finds nothing in some Win32 dialogs (seen on Inventor's migration dialog), so the child windows
		// are read directly. See Docs/Research/Blocking-Dialog-Detection.md, "The migration dialog".
		if (buttons.Count == 0 && window.ClassName == _win32DialogClass)
		{
			List<Win32Control> controls = Win32Controls(window.Handle);

			buttons = [.. controls.Where(control => control.IsButton).Select(control => control.ToButton())];
			texts.AddRange(controls
				.Where(control => control.IsStaticText && control.Visible && control.Text.Length > 0 && texts.Contains(control.Text) is false)
				.Select(control => control.Text));
		}

		return new DialogSnapshot(
			window.Handle.ToInt64(),
			window.Title,
			window.ClassName,
			content.Framework,
			string.Join(_textSeparator, texts),
			buttons);
	}

	/// <summary>
	/// 	Reads the buttons of a dialog from its Win32 child windows only, with no UI Automation.
	/// </summary>
	/// <remarks>
	/// 	For the desktop tests, which have no dialog that hides its buttons from UI Automation.
	/// </remarks>
	internal static IReadOnlyList<DialogButton> ReadWin32Buttons(long dialog) =>
		[.. Win32Controls(new IntPtr(dialog)).Where(control => control.IsButton).Select(control => control.ToButton())];

	/// <summary>
	/// 	Reads the text and the buttons of one dialog.
	/// </summary>
	/// <remarks>
	/// 	The UI Automation tree of a window also holds the windows it owns, so a walk stops at each element of a different
	/// 	top level window. Otherwise a dialog over a dialog mixes their text and buttons.
	/// 	UI Automation from the dialog can also miss buttons that are windows of their own (seen on the DevExpress buttons
	/// 	of the iLogic error dialog), so the Win32 child windows are read too, each from its own handle.
	/// 	See <c>Docs/Research/Blocking-Dialog-Detection.md</c>, "Live test results".
	/// </remarks>
	private DialogContent Collect(IntPtr dialog)
	{
		IUIAutomation automation = _automation.Value;
		IUIAutomationElement root = automation.ElementFromHandle(dialog);
		DialogContent content = new(root.CurrentFrameworkId);
		HashSet<IntPtr> seenHandles = [];

		Walk(automation.RawViewWalker, root, dialog, UIA_ControlTypeIds.UIA_WindowControlTypeId, content, seenHandles, 0);

		foreach (IntPtr child in ChildWindows(dialog))
		{
			if (seenHandles.Contains(child))
				continue;

			IUIAutomationElement element = automation.ElementFromHandle(child);

			if (element.CurrentControlType == UIA_ControlTypeIds.UIA_ButtonControlTypeId)
				content.Buttons.Add((ReadButton(element, child, UIA_ControlTypeIds.UIA_PaneControlTypeId), element));
		}

		return content;
	}

	private void Walk(IUIAutomationTreeWalker walker, IUIAutomationElement parent, IntPtr dialog, int parentType, DialogContent content, HashSet<IntPtr> seenHandles, int depth)
	{
		const int maxDepth = 40;

		for (IUIAutomationElement? element = walker.GetFirstChildElement(parent); element is not null; element = walker.GetNextSiblingElement(element))
		{
			IntPtr handle = element.CurrentNativeWindowHandle;

			if (handle != IntPtr.Zero)
			{
				if (GetAncestor(handle, _getAncestorRoot) != dialog)
					continue;

				_ = seenHandles.Add(handle);
			}

			int controlType = element.CurrentControlType;

			if (controlType == UIA_ControlTypeIds.UIA_ButtonControlTypeId)
			{
				content.Buttons.Add((ReadButton(element, handle, parentType), element));
			}
			else if (controlType is UIA_ControlTypeIds.UIA_TextControlTypeId or UIA_ControlTypeIds.UIA_EditControlTypeId or UIA_ControlTypeIds.UIA_DocumentControlTypeId)
			{
				// Some controls repeat the text of their parent, so each text is kept one time.
				string text = ReadText(element);

				if (text.Length > 0 && content.Texts.Contains(text) is false)
					content.Texts.Add(text);
			}

			if (depth < maxDepth)
				Walk(walker, element, dialog, controlType, content, seenHandles, depth + 1);
		}
	}

	/// <summary>
	/// 	Reads the state of one button.
	/// </summary>
	/// <remarks>
	/// 	WinForms controls have their own window, and UI Automation reports hidden ones as on screen.
	/// 	WPF controls, and the buttons of title bars and scroll bars, have no window, so IsOffscreen decides for them.
	/// 	A button of a title bar or a scroll bar (ex. <c>Close</c>, <c>Line down</c>) answers nothing, and a scroll bar
	/// 	appears when a message is long, as in the iLogic error dialog.
	/// </remarks>
	private static DialogButton ReadButton(IUIAutomationElement element, IntPtr handle, int parentType)
	{
		bool visible = handle != IntPtr.Zero ? IsWindowVisible(handle) : element.CurrentIsOffscreen == 0;
		bool enabled = element.CurrentIsEnabled != 0 && (handle == IntPtr.Zero || IsWindowEnabled(handle));
		bool windowFrame = handle == IntPtr.Zero
			&& parentType is UIA_ControlTypeIds.UIA_TitleBarControlTypeId or UIA_ControlTypeIds.UIA_ScrollBarControlTypeId;

		return new DialogButton(element.CurrentName ?? string.Empty, visible, enabled, windowFrame);
	}

	private static List<IntPtr> ChildWindows(IntPtr parent)
	{
		List<IntPtr> children = [];

		_ = EnumChildWindows(parent, (child, parameter) =>
		{
			children.Add(child);
			return true;
		}, IntPtr.Zero);

		return children;
	}

	/// <summary>
	/// 	Reads the text of a control.
	/// </summary>
	/// <remarks>
	/// 	Edit and document controls keep their text in a pattern, not in the name.
	/// </remarks>
	private static string ReadText(IUIAutomationElement element)
	{
		if (element.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) is IUIAutomationValuePattern value
			&& string.IsNullOrEmpty(value.CurrentValue) is false)
		{
			return value.CurrentValue;
		}

		if (element.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId) is IUIAutomationTextPattern textPattern)
		{
			string text = textPattern.DocumentRange.GetText(-1);

			if (string.IsNullOrEmpty(text) is false)
				return text;
		}

		return element.CurrentName ?? string.Empty;
	}

	/// <summary>
	/// 	Runs a UI Automation call with the time limit.
	/// </summary>
	/// <remarks>
	/// 	A dialog whose thread does not answer must not stop the caller.
	/// 	The call itself ends later, at the UI Automation transaction timeout.
	/// </remarks>
	private T WithTimeout<T>(Func<T> work) => Task.Run(work).WaitAsync(_readTimeout).GetAwaiter().GetResult();

	private static void WaitForClose(IntPtr handle)
	{
		DateTime deadline = DateTime.UtcNow + _closeWait;

		while (IsOpen(handle) && DateTime.UtcNow < deadline)
			Thread.Sleep(20);
	}

	private static ClickOutcome DialogClosedOutcome(DialogSnapshot dialog) =>
		new(ClickStatus.DialogClosed, $"'{dialog.Title}' closed before the click, ex. a different server or the user closed it.");

	private static DialogSnapshot Unread(WindowInfo window, string reason) =>
		new(window.Handle.ToInt64(), window.Title, window.ClassName, null, string.Empty, [], reason);

	private static IUIAutomation CreateAutomation(TimeSpan timeout)
	{
		CUIAutomation8 automation = new();

		// Without these, a call to a window that does not answer waits for the default of 20 s.
		if (automation is IUIAutomation2 withTimeouts)
		{
			withTimeouts.ConnectionTimeout = (uint)timeout.TotalMilliseconds;
			withTimeouts.TransactionTimeout = (uint)timeout.TotalMilliseconds;
		}

		return automation;
	}

	private static bool IsOpen(IntPtr handle) => IsWindow(handle) && IsWindowVisible(handle);

	private static List<WindowInfo> VisibleTopLevelWindows(int processId)
	{
		List<WindowInfo> windows = [];

		_ = EnumWindows((handle, parameter) =>
		{
			_ = GetWindowThreadProcessId(handle, out uint owner);

			if (owner == processId && IsWindowVisible(handle))
				windows.Add(new WindowInfo(handle, GetClassName(handle), GetTitle(handle), IsWindowEnabled(handle)));

			return true;
		}, IntPtr.Zero);

		return windows;
	}

	/// <remarks>
	/// 	For a window of a different process, GetWindowText reads the stored title and sends no message.
	/// 	So it does not wait on a thread that does not answer.
	/// </remarks>
	private static string GetTitle(IntPtr handle)
	{
		StringBuilder title = new(1024);
		_ = GetWindowText(handle, title, title.Capacity);

		return title.ToString();
	}

	private static string GetClassName(IntPtr handle)
	{
		StringBuilder className = new(256);
		_ = GetClassName(handle, className, className.Capacity);

		return className.ToString();
	}

	private sealed record WindowInfo(IntPtr Handle, string ClassName, string Title, bool Enabled);

	/// <summary>
	/// 	One Win32 child window of a dialog.
	/// </summary>
	private sealed record Win32Control(IntPtr Handle, int Id, string ClassName, string Text, bool Visible, bool Enabled)
	{
		public bool IsButton => ClassName == "Button";

		public bool IsStaticText => ClassName == "Static";

		/// <summary>
		/// 	The text without the mnemonic marker (ex. <c>&amp;Help</c> is <c>Help</c>), the same as UI Automation names it.
		/// </summary>
		public string Name => Text.Replace("&&", "\u0001").Replace("&", string.Empty).Replace("\u0001", "&");

		public DialogButton ToButton() => new(Name, Visible, Enabled, ControlId: Id);
	}

	/// <summary>
	/// 	What <see cref="Collect"/> found in one dialog.
	/// </summary>
	private sealed class DialogContent(string? framework)
	{
		public string? Framework { get; } = framework;

		public List<string> Texts { get; } = [];

		public List<(DialogButton Button, IUIAutomationElement Element)> Buttons { get; } = [];
	}

	[DllImport("user32.dll")]
	private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsCallback callback, IntPtr parameter);

	[DllImport("user32.dll")]
	private static extern IntPtr GetAncestor(IntPtr window, uint flags);

	[DllImport("user32.dll")]
	private static extern int GetDlgCtrlID(IntPtr window);

	[DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
	private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMilliseconds, out IntPtr result);

	private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);


	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(IntPtr window);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr window);

	[DllImport("user32.dll")]
	private static extern bool IsWindowEnabled(IntPtr window);

	[DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
	private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

	[DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);
}