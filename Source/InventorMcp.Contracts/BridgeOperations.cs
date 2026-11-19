namespace InventorMcp.Contracts;

/// <summary>
/// 	Names of the primitive operations the add-in handles. Each name is a JSON-RPC method on the pipe.
/// </summary>
/// <remarks>
/// 	One operation maps to one handler in the add-in dispatch table.
/// 	Shaping and composition belong in the server, because changing the add-in costs an Inventor restart.
/// </remarks>
public static class BridgeOperations
{
	/// <summary>
	/// 	Answers "pong" with no Inventor call.
	/// </summary>
	/// <remarks>
	/// 	Not "ping": MCP has a method of that name, and the SDK answers it before a handler of the add-in can.
	/// 	A new name must not be a method of MCP either (ex. <c>initialize</c>, or a name with a slash).
	/// </remarks>
	public const string Ping = "bridge.ping";

	public const string Session = "session";
	public const string Documents = "documents";
	public const string AssemblyTree = "assembly.tree";
	public const string Parameters = "parameters";
	public const string EvaluateExpression = "parameters.evaluate";
	public const string Properties = "properties";
	public const string SetProperty = "properties.set";
	public const string Health = "health";
	public const string Update = "update";
	public const string Activity = "activity";
	public const string RunILogic = "run.ilogic";
	public const string EvalCSharp = "run.csharp";

	/// <summary>
	/// 	Tells if an operation never uses Inventor's main thread.
	/// </summary>
	/// <remarks>
	/// 	Such an operation does not wait behind another call: the server sends it at once, and the add-in runs it at once.
	/// 	So <c>inventor_activity</c> answers while a long call runs.
	/// 	An operation in this list must never call the main thread dispatcher of the add-in. A call in the modal loop of a
	/// 	dialog would then run nested inside the call that opened the dialog.
	/// </remarks>
	/// <param name="operation">
	/// 	A name from this class.
	/// </param>
	/// <returns>
	/// 	True for <see cref="Ping"/> and <see cref="Activity"/>.
	/// </returns>
	public static bool RunsWithoutMainThread(string operation) =>
		operation is Ping or Activity;
}
