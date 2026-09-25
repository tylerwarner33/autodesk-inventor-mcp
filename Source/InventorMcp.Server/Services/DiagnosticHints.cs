using System.Text.RegularExpressions;

using InventorMcp.Contracts.Models;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	Makes the compiler diagnostics of a snippet more useful to a model.
/// </summary>
/// <remarks>
/// 	A wrong member name was the most common compile failure, and the model then sent a near copy of the snippet with
/// 	a guess. So a hint names the members that exist. A nullable warning in a successful result is only noise.
/// 	See <c>Docs/Research/Usage-Findings-And-Knowledge-Delivery.md</c>, "Failure classes".
/// </remarks>
internal static partial class DiagnosticHints
{
	private const int _maxHintMembers = 5;

	/// <summary>
	/// 	Removes noise from a successful result, and adds the near members to a missing member error.
	/// </summary>
	/// <param name="result">
	/// 	The result from the add-in.
	/// </param>
	/// <param name="apiReference">
	/// 	The API documentation.
	/// </param>
	/// <param name="releaseYear">
	/// 	The connected Inventor release, or null.
	/// </param>
	/// <returns>
	/// 	The same result, with the diagnostics changed.
	/// </returns>
	public static ExecutionResult Improve(ExecutionResult result, ApiReferenceService apiReference, int? releaseYear)
	{
		if (result.Succeeded)
		{
			// CS8632: a nullable annotation in a script with no nullable context. It changes nothing.
			return result.Diagnostics.Any(IsNullableNoise)
				? result with { Diagnostics = [.. result.Diagnostics.Where(diagnostic => IsNullableNoise(diagnostic) is false)] }
				: result;
		}

		List<string> hints = [];
		HashSet<string> seen = new(StringComparer.Ordinal);

		foreach (string diagnostic in result.Diagnostics)
		{
			Match match = MissingMember().Match(diagnostic);

			if (match.Success is false)
				continue;

			string typeName = match.Groups["type"].Value;
			string memberName = match.Groups["member"].Value;

			if (seen.Add($"{typeName}.{memberName}") && Hint(typeName, memberName, apiReference, releaseYear) is string hint)
				hints.Add(hint);
		}

		return hints.Count == 0 ? result : result with { Diagnostics = [.. result.Diagnostics, .. hints] };
	}

	/// <returns>
	/// 	The hint, or null when the documentation knows nothing near, ex. for a type outside the Inventor API.
	/// </returns>
	private static string? Hint(string typeName, string memberName, ApiReferenceService apiReference, int? releaseYear)
	{
		(IReadOnlyList<ApiMember> near, IReadOnlyList<ApiMember> elsewhere, int elsewhereCount) = apiReference.FindNear(typeName, memberName, _maxHintMembers, releaseYear);

		if (near.Count == 0 && elsewhere.Count == 0)
			return null;

		string text = $"HINT: '{typeName}' has no member '{memberName}'.";

		if (near.Count > 0)
			text += $" Members of '{typeName}' with a near name: {string.Join(", ", near.Select(static member => $"{member.Name} ({member.Kind})"))}.";

		// A member typed as object (ex. Parameter.Value) needs a cast before its members can be used.
		if (elsewhere.Count > 0)
		{
			text += elsewhereCount > elsewhere.Count
				? $" '{memberName}' is a member of {elsewhereCount} other types, ex. {string.Join(", ", elsewhere.Select(static member => member.QualifiedName))}."
				: $" '{memberName}' is a member of: {string.Join(", ", elsewhere.Select(static member => member.QualifiedName))}.";

			text += $" Find which member of '{typeName}' leads to one of them with inventor_api_lookup.";
		}

		return text;
	}

	/// <summary>
	/// 	Finds a COM failure whose message says nothing about the cause: E_FAIL (0x80004005), or the text of a
	/// 	late-bound call that failed.
	/// </summary>
	/// <remarks>
	/// 	The common cause is a write to a document that is not modifiable, so the caller adds the document state.
	/// 	A write to a library part through <c>UserParameters.AddByExpression</c> gave the late-bound text, not E_FAIL.
	/// </remarks>
	public static bool IsUnspecifiedComFailure(ExecutionResult result) =>
		result.Succeeded is false
		&& result.ExceptionMessage is string message
		&& (message.Contains("0x80004005", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("E_FAIL", StringComparison.Ordinal)
			|| message.StartsWith("Unspecified error", StringComparison.OrdinalIgnoreCase)
			|| (result.ExceptionType?.EndsWith("COMException", StringComparison.Ordinal) is true
				&& message.StartsWith("Exception has been thrown by the target of an invocation", StringComparison.Ordinal)));

	private static bool IsNullableNoise(string diagnostic) => diagnostic.Contains("CS8632", StringComparison.Ordinal);

	/// <summary>
	/// 	CS1061 (instance member) and CS0117 (static member or enum value), ex.
	/// 	<c>error CS1061: 'Balloon' does not contain a definition for 'RangeBox'</c>.
	/// </summary>
	[GeneratedRegex(@"error CS(?:1061|0117): '(?<type>[\w.]+)' does not contain a definition for '(?<member>\w+)'", RegexOptions.CultureInvariant)]
	private static partial Regex MissingMember();
}