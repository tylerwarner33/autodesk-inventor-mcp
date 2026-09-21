using System.Runtime.InteropServices;

using Inventor;

using InventorMcp.AddIn.Bridge;
using InventorMcp.AddIn.Operations;

namespace InventorMcp.AddIn;

/// <summary>
/// 	Entry point Inventor loads through the .addin manifest.
/// </summary>
/// <remarks>
/// 	The add-in owns only the bridge: the pipe listener, main thread marshaling, and the handler table.
/// 	Every MCP tool lives in the server process, because changing this assembly costs an Inventor restart.
/// </remarks>
[Guid("D827FC6D-39D8-495F-89AE-64E56F845FFC")]
[ComVisible(true)]
public sealed class StandardAddInServer : ApplicationAddInServer
{
	private Application? _inventor;
	private MainThreadDispatcher? _dispatcher;
	private BridgeServer? _bridge;
	private ActivityRecorder? _activity;

	/// <summary>
	/// 	Called by Inventor on its main thread when the add-in loads.
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
			_inventor = addInSiteObject.Application;

			// Created here so the message only window belongs to Inventor's main thread.
			_dispatcher = new MainThreadDispatcher();
			_dispatcher.Start();

			_activity = new ActivityRecorder(_inventor);
			_activity.Start();

			OperationDispatcher operations = new(BridgeLog.Write);
			InventorOperations.Register(operations, _inventor, _dispatcher, _activity);

			_bridge = new BridgeServer(operations, BridgeLog.Write);
			_bridge.Start();

			// Fully qualified: Inventor.Environment is the ribbon environment type, so the name collides.
			BridgeLog.Write($"Bridge started on pipe '{Contracts.BridgeProtocol.PipeName}' in process {System.Environment.ProcessId}.");
		}
		catch (Exception exception)
		{
			BridgeLog.Write($"Activate failed: {exception}");
			throw;
		}
	}

	/// <summary>
	/// 	Called by Inventor when the add-in unloads.
	/// </summary>
	public void Deactivate()
	{
		BridgeLog.Write("Bridge stopping.");

		_bridge?.Dispose();
		_bridge = null;

		_activity?.Dispose();
		_activity = null;

		_dispatcher?.Dispose();
		_dispatcher = null;

		// Release the application wrapper explicitly before collecting.
		// A surviving reference can keep the Inventor process alive after the user closes it.
		if (_inventor is not null && Marshal.IsComObject(_inventor))
		{
			try
			{
				_ = Marshal.ReleaseComObject(_inventor);
			}
			catch (Exception exception)
			{
				BridgeLog.Write($"Could not release the Inventor application object. {exception.Message}");
			}
		}

		_inventor = null;

		GC.Collect();
		GC.WaitForPendingFinalizers();
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

	/// <summary>
	/// 	Automation object exposed to other clients. The bridge uses a named pipe instead, so none is offered.
	/// </summary>
	public object? Automation => null;
}
