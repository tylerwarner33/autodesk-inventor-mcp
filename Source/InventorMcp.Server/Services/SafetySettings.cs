namespace InventorMcp.Server.Services;

/// <summary>
/// 	The limits that a user puts on the server through the environment of its client entry.
/// </summary>
/// <remarks>
/// 	A model cannot change them, because the server reads them one time, at start.
/// 	See <c>README.md</c>, "Limit what a model can do".
/// </remarks>
/// <param name="ReadOnlyTools">
/// 	True to offer only the tools that change no document.
/// </param>
/// <param name="ConfirmRiskyCalls">
/// 	True to ask the user before a call that removes a guard, answers a dialog or closes documents.
/// </param>
/// <param name="Problem">
/// 	What is wrong with a value, for the server log, or null.
/// </param>
internal sealed record SafetySettings(bool ReadOnlyTools, bool ConfirmRiskyCalls, string? Problem = null)
{
	/// <summary>
	/// 	The variable that selects the tools: <c>all</c> (the default) or <c>read-only</c>.
	/// </summary>
	public const string ToolsVariable = "INVENTORMCP_TOOLS";

	/// <summary>
	/// 	The variable that turns the confirmation of risky calls on with <c>true</c>.
	/// </summary>
	public const string ConfirmVariable = "INVENTORMCP_CONFIRM_RISKY_CALLS";

	private const string AllTools = "all";

	private const string ReadOnly = "read-only";

	/// <summary>
	/// 	Reads the settings.
	/// </summary>
	/// <remarks>
	/// 	A value that is not known selects the stricter setting, so a spelling error never gives a model more tools.
	/// </remarks>
	/// <param name="read">
	/// 	Gives the value of an environment variable, or null.
	/// </param>
	/// <returns>
	/// 	The settings.
	/// </returns>
	public static SafetySettings Read(Func<string, string?> read)
	{
		string? tools = read(ToolsVariable)?.Trim();
		string? confirm = read(ConfirmVariable)?.Trim();

		bool toolsKnown = string.IsNullOrEmpty(tools)
			|| string.Equals(tools, AllTools, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(tools, ReadOnly, StringComparison.OrdinalIgnoreCase);

		bool confirmKnown = string.IsNullOrEmpty(confirm) || bool.TryParse(confirm, out _);

		string? problem = (toolsKnown, confirmKnown) switch
		{
			(false, _) => $"{ToolsVariable} must be '{AllTools}' or '{ReadOnly}'. The server uses '{ReadOnly}'.",
			(_, false) => $"{ConfirmVariable} must be 'true' or 'false'. The server uses 'true'.",
			_ => null
		};

		return new SafetySettings(
			ReadOnlyTools: toolsKnown is false || string.Equals(tools, ReadOnly, StringComparison.OrdinalIgnoreCase),
			ConfirmRiskyCalls: confirmKnown is false || (bool.TryParse(confirm, out bool value) && value),
			problem);
	}
}
