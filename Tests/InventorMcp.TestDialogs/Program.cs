using System.Runtime.InteropServices;
using System.Windows.Interop;

using WinForms = System.Windows.Forms;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;

namespace InventorMcp.TestDialogs;

/// <summary>
/// 	Shows one modal dialog over a main window, the same way a dialog blocks the Inventor main frame.
/// </summary>
/// <remarks>
/// 	Usage: <c>InventorMcp.TestDialogs.exe &lt;mode&gt; [--close-after &lt;ms&gt;]</c>.
/// 	Writes <c>READY &lt;main window handle&gt; &lt;dialog handle&gt;</c> to standard output when the dialog is visible,
/// 	with handles in hexadecimal and 0 for no dialog.
/// 	The process exits when the dialog closes, except in the mode <c>none</c>, which runs until it is stopped.
/// 	See <c>Docs/Tasks/Server-Test-Project-Plan.md</c>, "The fixture process".
/// </remarks>
internal static class Program
{
	public const string MainWindowTitle = "InventorMcp Test Main";

	private const uint _getWindowOwner = 4;
	private const uint _windowMessageClose = 0x0010;

	[STAThread]
	private static int Main(string[] args)
	{
		if (args.Length == 0)
		{
			Console.Error.WriteLine("Give a mode: none, ok, yesno, okcancel, ilogic-like, wpf, unknown-ok, advisor, advisor-folder or hang.");
			return 2;
		}

		string mode = args[0];
		int? closeAfter = args.Length >= 3 && args[1] == "--close-after" ? int.Parse(args[2]) : null;

		WinForms.Application.EnableVisualStyles();

		using WinForms.Form main = new()
		{
			Text = MainWindowTitle,
			Width = 400,
			Height = 300,
			StartPosition = WinForms.FormStartPosition.CenterScreen
		};

		main.Shown += (_, _) =>
		{
			if (mode == "none")
			{
				Ready(main.Handle, IntPtr.Zero);
				return;
			}

			// The watch timer runs inside the modal loop of the dialog, because every modal loop pumps messages.
			WinForms.Timer watch = new() { Interval = 50 };

			watch.Tick += (_, _) =>
			{
				IntPtr dialog = FindOwnedWindow(main.Handle);

				if (dialog == IntPtr.Zero)
					return;

				watch.Stop();
				Ready(main.Handle, dialog);

				if (closeAfter is int milliseconds)
				{
					WinForms.Timer close = new() { Interval = milliseconds };
					close.Tick += (_, _) =>
					{
						close.Stop();
						_ = PostMessage(dialog, _windowMessageClose, IntPtr.Zero, IntPtr.Zero);
					};
					close.Start();
				}

				// The dialog stays on screen, but its thread answers no message, so UI Automation gets no answer.
				if (mode == "hang")
					Thread.Sleep(TimeSpan.FromSeconds(30));
			};

			watch.Start();
			ShowDialog(mode, main);
			main.Close();
		};

		WinForms.Application.Run(main);

		return 0;
	}

	private static void ShowDialog(string mode, WinForms.Form owner)
	{
		switch (mode)
		{
			case "ok":
			case "hang":
				_ = WinForms.MessageBox.Show(owner, "Test message", "Test OK");
				break;

			case "yesno":
				_ = WinForms.MessageBox.Show(owner, "Save?", "Test Question", WinForms.MessageBoxButtons.YesNo);
				break;

			case "okcancel":
				_ = WinForms.MessageBox.Show(owner, "Continue?", "Test OK Cancel", WinForms.MessageBoxButtons.OKCancel);
				break;

			case "ilogic-like":
				using (WinForms.Form dialog = CreateILogicLikeDialog())
					_ = dialog.ShowDialog(owner);
				break;

			case "unknown-ok":
				using (WinForms.Form dialog = CreateSimpleDialog("Custom Tool Message", "Something happened."))
					_ = dialog.ShowDialog(owner);
				break;

			case "wpf":
				ShowWpfDialog(owner);
				break;

			case "advisor":
			case "advisor-folder":
				using (WinForms.Form dialog = CreateAdvisorLikeDialog(trustFolder: mode == "advisor-folder"))
					_ = dialog.ShowDialog(owner);
				break;

			default:
				throw new ArgumentException($"Unknown mode '{mode}'.", nameof(mode));
		}
	}

	/// <summary>
	/// 	A copy of the iLogic error dialog: a message tab, a stack trace tab that is not selected, one visible OK, and
	/// 	hidden template buttons.
	/// </summary>
	/// <remarks>
	/// 	See <c>Docs/Research/Blocking-Dialog-Detection.md</c>, "The iLogic error dialog".
	/// </remarks>
	private static WinForms.Form CreateILogicLikeDialog()
	{
		WinForms.Form dialog = new()
		{
			Text = "Error on line 1 in rule: Test, in document: Test.ipt",
			Width = 520,
			Height = 320,
			FormBorderStyle = WinForms.FormBorderStyle.FixedDialog,
			MinimizeBox = false,
			MaximizeBox = false,
			StartPosition = WinForms.FormStartPosition.CenterParent
		};

		WinForms.TabControl tabs = new() { Left = 10, Top = 10, Width = 480, Height = 200 };
		// Long enough for a scroll bar, whose arrow buttons UI Automation also reports, as in the real dialog.
		string message = "RunExternalRule: Cannot find an external rule file named: \"DoesNotExist\"" +
			string.Concat(Enumerable.Range(1, 30).Select(line => $"\r\nLine {line} of the message."));
		tabs.TabPages.Add(CreateTextPage("Error Message", message));
		tabs.TabPages.Add(CreateTextPage("More Info", "System.Exception: Test stack trace\r\n   at ThisRule.Main() in rule: Test, line 1"));
		dialog.Controls.Add(tabs);

		WinForms.Button ok = new() { Text = "OK", Left = 410, Top = 225, DialogResult = WinForms.DialogResult.OK };
		dialog.Controls.Add(ok);
		dialog.AcceptButton = ok;

		foreach (string hidden in new[] { "Apply", "Extra", "Second", "Cancel" })
			dialog.Controls.Add(new WinForms.Button { Text = hidden, Visible = false });

		return dialog;
	}

	/// <summary>
	/// 	A copy of the iLogic Security Advisor: two radio buttons, OK, and three other buttons, one of them with no text.
	/// </summary>
	/// <remarks>
	/// 	See <c>Docs/Research/Blocking-Dialog-Detection.md</c>, "The iLogic Security Alert".
	/// </remarks>
	private static WinForms.Form CreateAdvisorLikeDialog(bool trustFolder)
	{
		WinForms.Form dialog = new()
		{
			Text = "iLogic Security Advisor",
			Width = 360,
			Height = 230,
			FormBorderStyle = WinForms.FormBorderStyle.FixedDialog,
			MinimizeBox = false,
			MaximizeBox = false,
			StartPosition = WinForms.FormStartPosition.CenterParent
		};

		dialog.Controls.Add(new WinForms.Label { Text = "In the future:", Left = 10, Top = 10, AutoSize = true });
		dialog.Controls.Add(new WinForms.RadioButton { Text = "Assume that this external rule is safe", Left = 20, Top = 35, Width = 300, Checked = trustFolder is false });
		dialog.Controls.Add(new WinForms.RadioButton { Text = "Assume that all external rules in this folder are safe", Left = 20, Top = 60, Width = 320, Checked = trustFolder });
		dialog.Controls.Add(new WinForms.Label { Text = "To change  these options later:", Left = 60, Top = 100, AutoSize = true });
		dialog.Controls.Add(new WinForms.Button { Text = "Security Options", Left = 230, Top = 95, Width = 110 });
		dialog.Controls.Add(new WinForms.Button { Text = string.Empty, Left = 10, Top = 150, Width = 30 });
		dialog.Controls.Add(new WinForms.Button { Text = "<< Back", Left = 150, Top = 150 });

		WinForms.Button ok = new() { Text = "OK", Left = 250, Top = 150, DialogResult = WinForms.DialogResult.OK };
		dialog.Controls.Add(ok);
		dialog.AcceptButton = ok;

		return dialog;
	}

	private static WinForms.TabPage CreateTextPage(string title, string text)
	{
		WinForms.TabPage page = new(title);
		page.Controls.Add(new WinForms.TextBox
		{
			Multiline = true,
			ReadOnly = true,
			ScrollBars = WinForms.ScrollBars.Both,
			WordWrap = false,
			Text = text,
			Dock = WinForms.DockStyle.Fill
		});

		return page;
	}

	private static WinForms.Form CreateSimpleDialog(string title, string text)
	{
		WinForms.Form dialog = new()
		{
			Text = title,
			Width = 320,
			Height = 160,
			FormBorderStyle = WinForms.FormBorderStyle.FixedDialog,
			MinimizeBox = false,
			MaximizeBox = false,
			StartPosition = WinForms.FormStartPosition.CenterParent
		};

		dialog.Controls.Add(new WinForms.Label { Text = text, Left = 10, Top = 10, AutoSize = true });

		WinForms.Button ok = new() { Text = "OK", Left = 220, Top = 80, DialogResult = WinForms.DialogResult.OK };
		dialog.Controls.Add(ok);
		dialog.AcceptButton = ok;

		return dialog;
	}

	private static void ShowWpfDialog(WinForms.Form owner)
	{
		Wpf.Window dialog = new()
		{
			Title = "Test WPF",
			Width = 300,
			Height = 150,
			WindowStartupLocation = Wpf.WindowStartupLocation.CenterOwner
		};

		WpfControls.StackPanel panel = new();
		_ = panel.Children.Add(new WpfControls.TextBlock { Text = "WPF message text" });

		WpfControls.Button ok = new() { Content = "OK", IsDefault = true };
		ok.Click += (_, _) => dialog.DialogResult = true;
		_ = panel.Children.Add(ok);

		dialog.Content = panel;

		// A WPF dialog with a Win32 owner, the same as a WPF add-in dialog over the Inventor main frame.
		_ = new WindowInteropHelper(dialog) { Owner = owner.Handle };
		_ = dialog.ShowDialog();
	}

	private static void Ready(IntPtr main, IntPtr dialog)
	{
		Console.Out.WriteLine($"READY {main.ToInt64():X} {dialog.ToInt64():X}");
		Console.Out.Flush();
	}

	/// <summary>
	/// 	Finds a visible top level window of this thread that the main window owns.
	/// </summary>
	private static IntPtr FindOwnedWindow(IntPtr main)
	{
		IntPtr found = IntPtr.Zero;

		_ = EnumThreadWindows(GetCurrentThreadId(), (window, _) =>
		{
			if (window != main && IsWindowVisible(window) && GetWindow(window, _getWindowOwner) == main)
			{
				found = window;
				return false;
			}

			return true;
		}, IntPtr.Zero);

		return found;
	}

	private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

	[DllImport("user32.dll")]
	private static extern bool EnumThreadWindows(uint threadId, EnumWindowsCallback callback, IntPtr parameter);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr window);

	[DllImport("user32.dll")]
	private static extern IntPtr GetWindow(IntPtr window, uint command);

	[DllImport("user32.dll")]
	private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}