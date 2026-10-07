using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

using InventorMcp.Server.Services;

using Microsoft.Win32.SafeHandles;

namespace InventorMcp.Server.Bridge;

/// <summary>
/// 	Checks that the process at the other end of the bridge pipe is this user's Inventor.
/// </summary>
/// <remarks>
/// 	The pipe name is global to the machine, so a process of any user and any session can create it before Inventor does.
/// 	The add-in's pipe ACL protects the add-in, not the server, so the server checks the host itself.
/// 	The checks are the Windows session, the executable path against the install locations in the machine registry, and
/// 	the owner of the process.
/// 	An elevated Inventor can refuse its token to a server that is not elevated. Then the owner is not checked, because
/// 	the session and the executable path have matched, and only this user or an administrator can start an elevated
/// 	process in this session.
/// </remarks>
internal static class PipeHost
{
	private const uint _queryLimitedInformation = 0x1000;
	private const uint _tokenQuery = 0x0008;

	/// <summary>
	/// 	Checks the process that hosts the pipe.
	/// </summary>
	/// <param name="processId">
	/// 	The process from <c>GetNamedPipeServerProcessId</c>.
	/// </param>
	/// <returns>
	/// 	Why the process is not this user's Inventor, or null when it is.
	/// </returns>
	public static string? Check(int processId) =>
		Check(processId, InventorInstallations.RegisteredExecutablePaths());

	/// <summary>
	/// 	Checks the process that hosts the pipe against the given executable paths.
	/// </summary>
	/// <param name="processId">
	/// 	The process from <c>GetNamedPipeServerProcessId</c>.
	/// </param>
	/// <param name="executablePaths">
	/// 	The full paths that an Inventor executable can have.
	/// </param>
	/// <returns>
	/// 	Why the process is not this user's Inventor, or null when it is.
	/// </returns>
	internal static string? Check(int processId, IReadOnlyList<string> executablePaths)
	{
		if (ProcessIdToSessionId((uint)processId, out uint hostSession) is false
			|| ProcessIdToSessionId((uint)Environment.ProcessId, out uint ownSession) is false)
		{
			return $"Windows did not give the session of process {processId}.";
		}

		if (hostSession != ownSession)
			return $"Process {processId} runs in Windows session {hostSession}, not in this user's session {ownSession}.";

		using SafeProcessHandle process = OpenProcess(_queryLimitedInformation, false, (uint)processId);

		if (process.IsInvalid)
			return $"Windows refused to open process {processId}, so it can belong to a different user.";

		string? imagePath = ReadImagePath(process);

		if (imagePath is null)
			return $"Windows did not give the executable of process {processId}.";

		if (executablePaths.Any(path => string.Equals(path, imagePath, StringComparison.OrdinalIgnoreCase)) is false)
			return $"Process {processId} runs '{imagePath}', which is not an installed Inventor.";

		SecurityIdentifier? owner = ReadOwner(process);
		SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;

		if (owner is not null && owner != currentUser)
			return $"Process {processId} belongs to {owner}, not to this user.";

		return null;
	}

	private static string? ReadImagePath(SafeProcessHandle process)
	{
		StringBuilder path = new(1024);
		int length = path.Capacity;

		return QueryFullProcessImageName(process, 0, path, ref length) ? Path.GetFullPath(path.ToString(0, length)) : null;
	}

	/// <summary>
	/// 	Reads the user of the process token, or null when Windows refuses the token.
	/// </summary>
	private static SecurityIdentifier? ReadOwner(SafeProcessHandle process)
	{
		if (OpenProcessToken(process, _tokenQuery, out SafeAccessTokenHandle token) is false)
			return null;

		using (token)
		{
			using WindowsIdentity identity = new(token.DangerousGetHandle());

			return identity.User;
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

	[DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);
}
