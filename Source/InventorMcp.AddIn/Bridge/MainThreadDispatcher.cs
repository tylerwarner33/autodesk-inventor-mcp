using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace InventorMcp.AddIn.Bridge;

/// <summary>
/// 	Runs work on Inventor's main thread.
/// </summary>
/// <remarks>
/// 	Inventor's COM objects belong to the main single threaded apartment.
/// 	Pipe requests arrive on worker threads, so every call into the Inventor API must be marshaled here first.
/// 	The dispatcher creates a message only window during <see cref="Start"/> and rides Inventor's existing message pump.
/// </remarks>
internal sealed class MainThreadDispatcher : IDisposable
{
	private const int WM_APP_WORK = 0x8000 + 1;
	private static readonly IntPtr HWND_MESSAGE = new(-3);

	private readonly ConcurrentQueue<Action> _work = new();
	private readonly string _windowClassName = "InventorMcpDispatcher_" + Guid.NewGuid().ToString("N");

	// RegisterClassEx stores a raw function pointer, so the delegate must stay reachable for the window's whole life.
	private readonly WndProcDelegate _wndProc;

	private IntPtr _windowHandle;
	private int _mainThreadId;
	private bool _disposed;

	public MainThreadDispatcher()
	{
		_wndProc = WindowProcedure;
	}

	/// <summary>
	/// 	Creates the message only window. Must be called from Inventor's main thread.
	/// </summary>
	public void Start()
	{
		_mainThreadId = Environment.CurrentManagedThreadId;

		WNDCLASSEX windowClass = new()
		{
			cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
			lpfnWndProc = _wndProc,
			hInstance = GetModuleHandle(null),
			lpszClassName = _windowClassName
		};

		if (RegisterClassEx(ref windowClass) is 0)
			throw new InvalidOperationException($"Could not register the dispatcher window class. Win32 error {Marshal.GetLastWin32Error()}.");

		_windowHandle = CreateWindowEx(
			0,
			_windowClassName,
			string.Empty,
			0, 0, 0, 0, 0,
			HWND_MESSAGE,
			IntPtr.Zero,
			GetModuleHandle(null),
			IntPtr.Zero);

		if (_windowHandle == IntPtr.Zero)
			throw new InvalidOperationException($"Could not create the dispatcher window. Win32 error {Marshal.GetLastWin32Error()}.");
	}

	/// <summary>
	/// 	Queues work onto Inventor's main thread and waits for its result.
	/// </summary>
	/// <remarks>
	/// 	Work posted from the main thread runs inline.
	/// 	Posting instead would block the thread that drains the queue, which deadlocks.
	/// </remarks>
	/// <typeparam name="TResult">
	/// 	Type the work returns.
	/// </typeparam>
	/// <param name="work">
	/// 	The work to run against the Inventor API.
	/// </param>
	/// <param name="cancellationToken">
	/// 	Cancels the wait. It does not stop work that has already started on the main thread.
	/// </param>
	/// <returns>
	/// 	The value the work produced.
	/// </returns>
	public Task<TResult> InvokeAsync<TResult>(Func<TResult> work, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		if (Environment.CurrentManagedThreadId == _mainThreadId)
		{
			try
			{
				return Task.FromResult(work());
			}
			catch (Exception exception)
			{
				return Task.FromException<TResult>(exception);
			}
		}

		TaskCompletionSource<TResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

		using CancellationTokenRegistration registration = cancellationToken.Register(
			static state => ((TaskCompletionSource<TResult>)state!).TrySetCanceled(),
			completion);

		_work.Enqueue(() =>
		{
			if (completion.Task.IsCompleted)
				return;

			try
			{
				completion.TrySetResult(work());
			}
			catch (Exception exception)
			{
				completion.TrySetException(exception);
			}
		});

		if (PostMessage(_windowHandle, WM_APP_WORK, IntPtr.Zero, IntPtr.Zero) is false)
			completion.TrySetException(new InvalidOperationException($"Could not post to the dispatcher window. Win32 error {Marshal.GetLastWin32Error()}."));

		return completion.Task;
	}

	private IntPtr WindowProcedure(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
	{
		if (message is WM_APP_WORK)
		{
			// Drain fully: several posts can coalesce into fewer messages under load.
			while (_work.TryDequeue(out Action? item))
			{
				try
				{
					item();
				}
				catch
				{
					// The work item already reported the failure through its completion source.
					// Letting an exception escape a WndProc would tear down Inventor.
				}
			}

			return IntPtr.Zero;
		}

		return DefWindowProc(windowHandle, message, wParam, lParam);
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;

		if (_windowHandle != IntPtr.Zero)
		{
			_ = DestroyWindow(_windowHandle);
			_windowHandle = IntPtr.Zero;
		}

		_ = UnregisterClass(_windowClassName, GetModuleHandle(null));

		while (_work.TryDequeue(out Action? _))
		{
			// Abandon queued work; the pipe side fails its pending requests on shutdown.
		}
	}

	private delegate IntPtr WndProcDelegate(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WNDCLASSEX
	{
		public uint cbSize;
		public uint style;
		[MarshalAs(UnmanagedType.FunctionPtr)]
		public WndProcDelegate lpfnWndProc;
		public int cbClsExtra;
		public int cbWndExtra;
		public IntPtr hInstance;
		public IntPtr hIcon;
		public IntPtr hCursor;
		public IntPtr hbrBackground;
		[MarshalAs(UnmanagedType.LPWStr)]
		public string? lpszMenuName;
		[MarshalAs(UnmanagedType.LPWStr)]
		public string lpszClassName;
		public IntPtr hIconSm;
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool UnregisterClass(string className, IntPtr instance);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateWindowEx(
		uint exStyle,
		string className,
		string windowName,
		uint style,
		int x,
		int y,
		int width,
		int height,
		IntPtr parent,
		IntPtr menu,
		IntPtr instance,
		IntPtr param);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool DestroyWindow(IntPtr windowHandle);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr DefWindowProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr GetModuleHandle(string? moduleName);
}
