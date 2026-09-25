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

	public ClickOutcome TryClick(int processId, DialogSnapshot dialog, string buttonName)
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
			return ClickWhileOwned(dialog, buttonName);
		}
		finally
		{
			mutex.ReleaseMutex();
		}
	}

	private ClickOutcome ClickWhileOwned(DialogSnapshot dialog, string buttonName)
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
			ClickOutcome outcome = WithTimeout(() => Invoke(handle, dialog.Title, buttonName));

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
		IUIAutomationElement window = _automation.Value.ElementFromHandle(handle);

		List<IUIAutomationElement> matches = [.. FindButtons(window)
			.Where(pair => pair.Button.IsClickable && pair.Button.Name == buttonName)
			.Select(pair => pair.Element)];

		if (matches.Count != 1)
			return new ClickOutcome(ClickStatus.Refused, $"'{title}' has {matches.Count} visible, enabled buttons named '{buttonName}', so nothing was clicked.");

		if (matches[0].GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) is not IUIAutomationInvokePattern invoke)
			return new ClickOutcome(ClickStatus.Failed, $"The button '{buttonName}' cannot be clicked through UI Automation.");

		invoke.Invoke();

		return new ClickOutcome(ClickStatus.Clicked, $"Clicked '{buttonName}' in '{title}'.");
	}

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
		IUIAutomation automation = _automation.Value;
		IUIAutomationElement element = automation.ElementFromHandle(window.Handle);
		IUIAutomationElementArray descendants = element.FindAll(TreeScope.TreeScope_Descendants, automation.CreateTrueCondition());

		List<DialogButton> buttons = [];
		List<string> texts = [];

		for (int index = 0; index < descendants.Length; index++)
		{
			IUIAutomationElement descendant = descendants.GetElement(index);
			int controlType = descendant.CurrentControlType;

			if (controlType == UIA_ControlTypeIds.UIA_ButtonControlTypeId)
			{
				buttons.Add(ReadButton(descendant));
			}
			else if (controlType is UIA_ControlTypeIds.UIA_TextControlTypeId or UIA_ControlTypeIds.UIA_EditControlTypeId or UIA_ControlTypeIds.UIA_DocumentControlTypeId)
			{
				// Some controls repeat the text of their parent, so each text is kept one time.
				string text = ReadText(descendant);

				if (text.Length > 0 && texts.Contains(text) is false)
					texts.Add(text);
			}
		}

		// A WPF button holds its caption as a text element, which is not dialog text.
		_ = texts.RemoveAll(text => buttons.Any(button => button.Name == text));

		return new DialogSnapshot(
			window.Handle.ToInt64(),
			window.Title,
			window.ClassName,
			element.CurrentFrameworkId,
			string.Join(_textSeparator, texts),
			buttons);
	}

	private IEnumerable<(DialogButton Button, IUIAutomationElement Element)> FindButtons(IUIAutomationElement window)
	{
		IUIAutomation automation = _automation.Value;
		IUIAutomationCondition condition = automation.CreatePropertyCondition(
			UIA_PropertyIds.UIA_ControlTypePropertyId,
			UIA_ControlTypeIds.UIA_ButtonControlTypeId);

		IUIAutomationElementArray found = window.FindAll(TreeScope.TreeScope_Descendants, condition);

		for (int index = 0; index < found.Length; index++)
		{
			IUIAutomationElement element = found.GetElement(index);

			yield return (ReadButton(element), element);
		}
	}

	private DialogButton ReadButton(IUIAutomationElement element)
	{
		IntPtr handle = element.CurrentNativeWindowHandle;

		// WinForms controls have their own window, and UI Automation reports hidden ones as on screen.
		// WPF controls and title bar buttons have no window, so IsOffscreen decides for them.
		bool visible = handle != IntPtr.Zero ? IsWindowVisible(handle) : element.CurrentIsOffscreen == 0;
		bool inTitleBar = handle == IntPtr.Zero
			&& _automation.Value.RawViewWalker.GetParentElement(element)?.CurrentControlType == UIA_ControlTypeIds.UIA_TitleBarControlTypeId;

		return new DialogButton(element.CurrentName ?? string.Empty, visible, element.CurrentIsEnabled != 0, inTitleBar);
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