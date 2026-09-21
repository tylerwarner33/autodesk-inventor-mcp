namespace InventorMcp.Contracts;

/// <summary>
/// 	Names of the primitive operations the add-in handles.
/// </summary>
/// <remarks>
/// 	One operation maps to one handler in the add-in dispatch table.
/// 	Shaping and composition belong in the server, because changing the add-in costs an Inventor restart.
/// </remarks>
public static class BridgeOperations
{
	public const string Ping = "ping";
	public const string Session = "session";
	public const string Documents = "documents";
	public const string AssemblyTree = "assembly.tree";
	public const string Parameters = "parameters";
	public const string SetParameter = "parameters.set";
	public const string EvaluateExpression = "parameters.evaluate";
	public const string Properties = "properties";
	public const string SetProperty = "properties.set";
	public const string Health = "health";
	public const string Update = "update";
	public const string Activity = "activity";
	public const string RunILogic = "run.ilogic";
	public const string EvalCSharp = "run.csharp";
}
