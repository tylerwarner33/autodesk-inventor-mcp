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
	internal static void Redirect() => ExecutionAuditLog.LogRoot = Folder;
}
