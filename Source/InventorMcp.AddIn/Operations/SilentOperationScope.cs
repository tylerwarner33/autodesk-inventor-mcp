using Inventor;

namespace InventorMcp.AddIn.Operations;

/// <summary>
/// 	Suppresses Inventor's interactive dialogs for the duration of a write.
/// </summary>
/// <remarks>
/// 	A modal dialog owns Inventor's message pump.
/// 	<see cref="Bridge.MainThreadDispatcher"/> posts work to that pump and waits, so a dialog nobody answers stalls
/// 	every bridge request until a human clicks it.
/// 	A style conflict or a locked design view representation is enough to trigger that.
///
/// 	Inventor answers a suppressed dialog with its own default, which is a real behaviour change rather than a
/// 	cosmetic one. A dialog whose default is wrong for the document now takes that wrong default silently.
/// 	So this is applied only around writes and executions, never around reads, and the previous value is always
/// 	restored.
/// </remarks>
internal readonly struct SilentOperationScope : IDisposable
{
	private readonly Application? _inventor;
	private readonly bool _previousValue;

	private SilentOperationScope(Application? inventor, bool previousValue)
	{
		_inventor = inventor;
		_previousValue = previousValue;
	}

	/// <summary>
	/// 	Turns dialog suppression on, remembering what it was.
	/// </summary>
	/// <param name="inventor">
	/// 	The running Inventor application.
	/// </param>
	/// <returns>
	/// 	A scope that restores the previous value when disposed.
	/// </returns>
	public static SilentOperationScope Enter(Application inventor)
	{
		try
		{
			bool previousValue = inventor.SilentOperation;
			inventor.SilentOperation = true;

			return new SilentOperationScope(inventor, previousValue);
		}
		catch (Exception exception)
		{
			// Losing suppression means a write may stall on a dialog. It is not a reason to refuse the write.
			BridgeLog.Write($"Could not set SilentOperation; a write may stall on a modal dialog. {exception.Message}");

			return new SilentOperationScope(null, false);
		}
	}

	public void Dispose()
	{
		if (_inventor is null)
			return;

		try
		{
			_inventor.SilentOperation = _previousValue;
		}
		catch (Exception exception)
		{
			BridgeLog.Write($"Could not restore SilentOperation. {exception.Message}");
		}
	}
}
