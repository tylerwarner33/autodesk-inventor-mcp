using System.Runtime.InteropServices;

using Inventor;

namespace InventorMcp.AddIn.Loader;

/// <summary>
/// 	What the Inventor 2025 and 2026 manifests name, forwarding every call to the add-in in its isolated context.
/// </summary>
/// <remarks>
/// 	Inventor creates add-in servers by class id, always in the default context, so the manifest names this proxy
/// 	rather than the add-in. The class id matches the add-in's; only one manifest is live in a given process.
/// 	The isolated instance is used through a direct interface cast, because the interop resolves from the default
/// 	context on both sides and <see cref="ApplicationAddInServer" /> is therefore one type.
/// </remarks>
[Guid("D827FC6D-39D8-495F-89AE-64E56F845FFC")]
[ComVisible(true)]
public sealed class IsolatedAddInServerProxy : ApplicationAddInServer
{
	private const string AddInServerTypeFullName = "InventorMcp.AddIn.StandardAddInServer";

	private ApplicationAddInServer? _isolatedServer;

	/// <summary>
	/// 	Automation object exposed to other clients, supplied by the isolated add-in.
	/// </summary>
	public object? Automation => _isolatedServer?.Automation;

	/// <summary>
	/// 	Loads the add-in into its isolated context and activates it.
	/// </summary>
	/// <param name="addInSiteObject">
	/// 	Gives access to the running Inventor application.
	/// </param>
	/// <param name="firstTime">
	/// 	True the first time the add-in loads after installation.
	/// </param>
	public void Activate(ApplicationAddInSite addInSiteObject, bool firstTime)
	{
		try
		{
			_isolatedServer = (ApplicationAddInServer)AddInLoadContext.CreateIsolatedInstance(AddInServerTypeFullName);
		}
		catch (Exception exception)
		{
			// The add-in's own log does not exist yet, and Inventor swallows this exception.
			StartupLog.WriteFailure("loading the isolated add-in", exception);

			throw;
		}

		_isolatedServer.Activate(addInSiteObject, firstTime);
	}

	/// <summary>
	/// 	Deactivates the isolated add-in.
	/// </summary>
	public void Deactivate()
	{
		_isolatedServer?.Deactivate();
		_isolatedServer = null;
	}

	/// <summary>
	/// 	Legacy command hook. Inventor no longer calls this.
	/// </summary>
	/// <param name="commandID">
	/// 	Identifier of the command Inventor would have run.
	/// </param>
	public void ExecuteCommand(int commandID)
	{
	}
}
