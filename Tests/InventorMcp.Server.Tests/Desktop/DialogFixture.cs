using System.Diagnostics;
using System.Globalization;

namespace InventorMcp.Server.Tests.Desktop;

/// <summary>
/// 	Starts <c>InventorMcp.TestDialogs.exe</c> in one mode and stops it at the end, also after a failure.
/// </summary>
internal sealed class DialogFixture : IDisposable
{
	/// <summary>
	/// 	The title of the fixture main window, which takes the place of the Inventor main frame.
	/// </summary>
	public const string MainWindowTitle = "InventorMcp Test Main";

	private static readonly TimeSpan _readyTimeout = TimeSpan.FromSeconds(15);

	private readonly Process _process;

	private DialogFixture(Process process, long mainHandle, long dialogHandle)
	{
		_process = process;
		MainHandle = mainHandle;
		DialogHandle = dialogHandle;
	}

	public int ProcessId => _process.Id;

	public long MainHandle { get; }

	public long DialogHandle { get; }

	public bool HasExited => _process.HasExited;

	/// <summary>
	/// 	Tests the fixture main window, for <see cref="Services.BlockingDialogs"/>.
	/// </summary>
	public static bool IsMainWindow(string className, string title) => title == MainWindowTitle;

	/// <summary>
	/// 	Starts the fixture and waits until its dialog is visible.
	/// </summary>
	/// <param name="arguments">
	/// 	The mode, and optionally <c>--close-after</c> and a time in milliseconds.
	/// </param>
	public static async Task<DialogFixture> StartAsync(params string[] arguments)
	{
		ProcessStartInfo startInfo = new(Path.Combine(AppContext.BaseDirectory, "InventorMcp.TestDialogs.exe"))
		{
			RedirectStandardOutput = true,
			UseShellExecute = false
		};

		foreach (string argument in arguments)
			startInfo.ArgumentList.Add(argument);

		Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("The fixture did not start.");

		try
		{
			string? line = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
				.AsTask()
				.WaitAsync(_readyTimeout, TestContext.Current.CancellationToken);

			string[] parts = line?.Split(' ') ?? [];

			if (parts is not ["READY", string main, string dialog])
				throw new InvalidOperationException($"The fixture wrote '{line}', not READY.");

			return new DialogFixture(
				process,
				long.Parse(main, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
				long.Parse(dialog, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
		}
		catch
		{
			Stop(process);
			throw;
		}
	}

	/// <summary>
	/// 	Waits for the fixture to exit, which it does when its dialog closes.
	/// </summary>
	public bool WaitForExit(TimeSpan timeout) => _process.WaitForExit(timeout);

	public void Dispose() => Stop(_process);

	private static void Stop(Process process)
	{
		if (process.HasExited is false)
		{
			process.Kill(entireProcessTree: true);
			_ = process.WaitForExit(TimeSpan.FromSeconds(5));
		}

		process.Dispose();
	}
}