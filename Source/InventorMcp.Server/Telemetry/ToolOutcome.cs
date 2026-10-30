namespace InventorMcp.Server.Telemetry;

/// <summary>
/// 	The outcome of one tool call, from <c>InventorTool.SafeAsync</c> to the call tool filter in <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// 	The filter puts a new object in the <see cref="AsyncLocal{T}"/>, and the tool below it in the same asynchronous flow
/// 	changes that object.
/// 	A value set in a child flow does not flow back to the parent, so the tool changes the object and never replaces it.
/// 	The same pattern as <c>DialogReports</c>.
/// </remarks>
internal sealed class ToolOutcome
{
	private static readonly AsyncLocal<ToolOutcome?> _current = new();

	/// <summary>
	/// 	The outcome of the current tool call, or null outside one.
	/// </summary>
	public static ToolOutcome? Current => _current.Value;

	/// <summary>
	/// 	A value of <c>TelemetryTags.Outcome*</c>.
	/// </summary>
	public string Outcome { get; private set; } = TelemetryTags.OutcomeOk;

	/// <summary>
	/// 	The value of <c>error.type</c>, or null when the outcome is ok.
	/// </summary>
	public string? ErrorType { get; private set; }

	/// <summary>
	/// 	The time that a snippet held Inventor's main thread, or null for a call that ran no snippet.
	/// </summary>
	public long? MainThreadMilliseconds { get; set; }

	/// <summary>
	/// 	Starts the outcome of a new tool call.
	/// </summary>
	/// <returns>
	/// 	The outcome, which the tool fills.
	/// </returns>
	public static ToolOutcome Begin()
	{
		ToolOutcome outcome = new();
		_current.Value = outcome;

		return outcome;
	}

	/// <summary>
	/// 	Records a failure.
	/// </summary>
	/// <param name="outcome">
	/// 	A value of <c>TelemetryTags.Outcome*</c>.
	/// </param>
	/// <param name="errorType">
	/// 	A value that is safe to export, from a <c>TelemetryTags.*Value</c> method.
	/// </param>
	/// <returns>
	/// 	False, so a <c>catch</c> filter can record the failure and not catch the exception.
	/// </returns>
	public bool Fail(string outcome, string errorType)
	{
		Outcome = outcome;
		ErrorType = errorType;

		return false;
	}
}
