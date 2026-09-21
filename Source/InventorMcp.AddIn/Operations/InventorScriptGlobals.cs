using Inventor;

namespace InventorMcp.AddIn.Operations;

/// <summary>
/// 	The objects a C# snippet can reach, and the log it can write to.
/// </summary>
/// <remarks>
/// 	This type must be public. Roslyn scripting binds the globals by their public surface,
/// 	and a script compiled into its own assembly cannot see an internal type.
/// </remarks>
public sealed class InventorScriptGlobals
{
	private readonly List<string> _output = [];

	/// <summary>
	/// 	The running Inventor application.
	/// </summary>
	public required Application Application { get; init; }

	/// <summary>
	/// 	The document the snippet targets, which is the active document unless one was named.
	/// </summary>
	public required Document Document { get; init; }

	/// <summary>
	/// 	Everything the snippet logged, in order.
	/// </summary>
	public IReadOnlyList<string> Output => _output;

	/// <summary>
	/// 	Records a line for the caller to read.
	/// </summary>
	/// <remarks>
	/// 	A snippet has no console, so this is the only way to report progress.
	/// 	Writing to standard output would corrupt the MCP protocol stream in the server process.
	/// </remarks>
	/// <param name="message">
	/// 	Value to record. Null is recorded as "null".
	/// </param>
	public void Log(object? message)
	{
		_output.Add(message?.ToString() ?? "null");
	}
}
