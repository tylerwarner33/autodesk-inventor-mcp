using System.Runtime.CompilerServices;

using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests;

/// <summary>
/// 	Sends the audit entries of every test to a temporary folder.
/// </summary>
/// <remarks>
/// 	The dialog tests click fake dialogs, and each click writes an audit entry. Without this, the entries land in the
/// 	user's real audit trail, where they look like real clicks.
/// </remarks>
internal static class AuditLogRedirect
{
	/// <summary>
	/// 	The folder that receives the audit entries of this test run.
	/// </summary>
	public static string Folder { get; } = Path.Combine(Path.GetTempPath(), "InventorMcp.Tests", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

	[ModuleInitializer]
	internal static void Redirect()
	{
		ExecutionAuditLog.LogRoot = Folder;
		AppDomain.CurrentDomain.ProcessExit += static (_, _) => Delete();
	}

	/// <summary>
	/// 	Deletes the folder of this run when the test process ends.
	/// </summary>
	/// <remarks>
	/// 	A failure is ignored, because a folder that is left behind only costs a little space.
	/// </remarks>
	private static void Delete()
	{
		try
		{
			if (Directory.Exists(Folder))
				Directory.Delete(Folder, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
	}
}
