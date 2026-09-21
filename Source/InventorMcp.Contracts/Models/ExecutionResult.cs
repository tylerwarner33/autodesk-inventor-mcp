namespace InventorMcp.Contracts.Models;

/// <summary>
/// 	Outcome of running code inside the Inventor session.
/// </summary>
/// <param name="Succeeded">
/// 	True when the code ran to completion without throwing.
/// </param>
/// <param name="ReturnValue">
/// 	The returned value rendered as text, or null when the code returned nothing.
/// </param>
/// <param name="ReturnType">
/// 	CLR type name of the returned value, or null.
/// </param>
/// <param name="Output">
/// 	Text the code wrote through the logging helper supplied to it.
/// </param>
/// <param name="Diagnostics">
/// 	Compiler diagnostics. Populated when compilation failed.
/// </param>
/// <param name="ExceptionType">
/// 	Type name of the thrown exception, or null.
/// </param>
/// <param name="ExceptionMessage">
/// 	Message of the thrown exception, or null.
/// </param>
/// <param name="ElapsedMilliseconds">
/// 	Wall clock time the execution took on Inventor's main thread.
/// </param>
public sealed record ExecutionResult(
	bool Succeeded,
	string? ReturnValue,
	string? ReturnType,
	IReadOnlyList<string> Output,
	IReadOnlyList<string> Diagnostics,
	string? ExceptionType,
	string? ExceptionMessage,
	long ElapsedMilliseconds);
