using System.ComponentModel;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	A process started with the shell's Explorer as its parent, so it outlives the server.
/// </summary>
/// <remarks>
/// 	The server is a child of the MCP client, and a tree kill or a job object of the client could reach anything the
/// 	server starts as its own child.
/// 	With <c>PROC_THREAD_ATTRIBUTE_PARENT_PROCESS</c> the new process takes its parent, job object and token from Explorer,
/// 	the same as a program started from the Start menu.
/// 	See <c>Docs/Architecture.md</c>.
///
/// 	Uses only the base class library, so it can be compiled alone to test the start method.
/// </remarks>
internal sealed class DetachedProcess : IDisposable
{
	private const uint _processCreateProcess = 0x0080;
	private const uint _extendedStartupInfoPresent = 0x00080000;
	private const uint _createUnicodeEnvironment = 0x00000400;
	private const int _procThreadAttributeParentProcess = 0x00020000;
	private const uint _waitObject0 = 0x00000000;
	private const uint _tokenQuery = 0x0008;
	private const uint _tokenDuplicate = 0x0002;

	private readonly SafeProcessHandle _handle;

	private DetachedProcess(SafeProcessHandle handle, int id)
	{
		_handle = handle;
		Id = id;
	}

	/// <summary>
	/// 	Process identifier.
	/// </summary>
	public int Id { get; }

	/// <summary>
	/// 	True once the process has ended.
	/// </summary>
	public bool HasExited => WaitForSingleObject(_handle, 0) == _waitObject0;

	/// <summary>
	/// 	Exit code, or null while the process runs.
	/// </summary>
	public int? ExitCode => HasExited && GetExitCodeProcess(_handle, out uint exitCode) ? unchecked((int)exitCode) : null;

	/// <summary>
	/// 	Starts a program with the shell's Explorer as its parent.
	/// </summary>
	/// <remarks>
	/// 	No handle is inherited.
	/// 	The server's standard output carries the MCP protocol, and a program holding it would keep the client's pipe open.
	/// 	The environment is built fresh for the user, so the program does not inherit the MCP client's variables.
	/// </remarks>
	/// <param name="executablePath">
	/// 	Full path of the program.
	/// </param>
	/// <param name="workingDirectory">
	/// 	Working directory, or null for the program's own folder.
	/// </param>
	/// <returns>
	/// 	The started process.
	/// </returns>
	/// <exception cref="Win32Exception">
	/// 	The shell is not running, or Windows refused a step.
	/// </exception>
	public static DetachedProcess Start(string executablePath, string? workingDirectory = null)
	{
		IntPtr shellWindow = GetShellWindow();

		if (shellWindow == IntPtr.Zero)
			throw new Win32Exception("The Windows shell is not running, so there is no Explorer process to start Inventor under.");

		_ = GetWindowThreadProcessId(shellWindow, out uint shellProcessId);

		using SafeProcessHandle shell = OpenProcess(_processCreateProcess, false, shellProcessId);

		if (shell.IsInvalid)
			throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open the Explorer process {shellProcessId}.");

		IntPtr attributeListSize = IntPtr.Zero;
		_ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);

		IntPtr attributeList = Marshal.AllocHGlobal(attributeListSize);
		IntPtr parentHandleValue = Marshal.AllocHGlobal(IntPtr.Size);
		IntPtr environment = IntPtr.Zero;
		bool attributeListInitialised = false;

		try
		{
			if (InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize) is false)
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not initialise the process attribute list.");

			attributeListInitialised = true;

			Marshal.WriteIntPtr(parentHandleValue, shell.DangerousGetHandle());

			if (UpdateProcThreadAttribute(attributeList, 0, _procThreadAttributeParentProcess, parentHandleValue, IntPtr.Size, IntPtr.Zero, IntPtr.Zero) is false)
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not set Explorer as the parent process.");

			environment = CreateUserEnvironment();

			StartupInfoEx startupInfo = new()
			{
				StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() },
				AttributeList = attributeList
			};

			// CreateProcess may write to the command line buffer, so it cannot be a string.
			char[] commandLine = $"\"{executablePath}\"\0".ToCharArray();

			uint creationFlags = _extendedStartupInfoPresent | (environment == IntPtr.Zero ? 0 : _createUnicodeEnvironment);

			if (CreateProcess(
				executablePath,
				commandLine,
				IntPtr.Zero,
				IntPtr.Zero,
				false,
				creationFlags,
				environment,
				workingDirectory ?? Path.GetDirectoryName(executablePath),
				ref startupInfo,
				out ProcessInformation processInformation) is false)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start '{executablePath}'.");
			}

			_ = CloseHandle(processInformation.Thread);

			return new DetachedProcess(new SafeProcessHandle(processInformation.Process, ownsHandle: true), processInformation.ProcessId);
		}
		finally
		{
			if (environment != IntPtr.Zero)
				_ = DestroyEnvironmentBlock(environment);

			if (attributeListInitialised)
				DeleteProcThreadAttributeList(attributeList);

			Marshal.FreeHGlobal(parentHandleValue);
			Marshal.FreeHGlobal(attributeList);
		}
	}

	/// <summary>
	/// 	Builds the user's environment from the registry, or returns zero to inherit the server's.
	/// </summary>
	private static IntPtr CreateUserEnvironment()
	{
		if (OpenProcessToken(GetCurrentProcess(), _tokenQuery | _tokenDuplicate, out IntPtr token) is false)
			return IntPtr.Zero;

		try
		{
			return CreateEnvironmentBlock(out IntPtr environment, token, false) ? environment : IntPtr.Zero;
		}
		finally
		{
			_ = CloseHandle(token);
		}
	}

	public void Dispose() => _handle.Dispose();

	[StructLayout(LayoutKind.Sequential)]
	private struct StartupInfo
	{
		public int Size;
		public IntPtr Reserved;
		public IntPtr Desktop;
		public IntPtr Title;
		public int X;
		public int Y;
		public int XSize;
		public int YSize;
		public int XCountChars;
		public int YCountChars;
		public int FillAttribute;
		public int Flags;
		public short ShowWindow;
		public short Reserved2Size;
		public IntPtr Reserved2;
		public IntPtr StandardInput;
		public IntPtr StandardOutput;
		public IntPtr StandardError;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct StartupInfoEx
	{
		public StartupInfo StartupInfo;
		public IntPtr AttributeList;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ProcessInformation
	{
		public IntPtr Process;
		public IntPtr Thread;
		public int ProcessId;
		public int ThreadId;
	}

	[DllImport("user32.dll")]
	private static extern IntPtr GetShellWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

	[DllImport("kernel32.dll")]
	private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

	[DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CreateProcess(
		string applicationName,
		char[] commandLine,
		IntPtr processAttributes,
		IntPtr threadAttributes,
		bool inheritHandles,
		uint creationFlags,
		IntPtr environment,
		string? currentDirectory,
		ref StartupInfoEx startupInfo,
		out ProcessInformation processInformation);

	[DllImport("kernel32.dll")]
	private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint exitCode);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetCurrentProcess();

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

	[DllImport("userenv.dll", SetLastError = true)]
	private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

	[DllImport("userenv.dll", SetLastError = true)]
	private static extern bool DestroyEnvironmentBlock(IntPtr environment);
}
