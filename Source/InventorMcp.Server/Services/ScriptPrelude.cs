using System.Text;
using System.Text.RegularExpressions;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	Puts the script helpers before a snippet that calls one of them.
/// </summary>
/// <remarks>
/// 	The helpers are sent with the snippet, not built into the add-in's script globals, so a change needs no add-in
/// 	rebuild. A snippet that calls no helper is sent as it is, so it pays no compile cost and the audit log stays short.
/// 	The snippet's own leading using directives go first, because C# allows them only before all other elements.
/// 	<c>#line</c> keeps the line numbers of the diagnostics the same as in the snippet.
/// 	See <c>Docs/Architecture.md</c>, "Script helpers travel with the snippet".
/// </remarks>
internal static partial class ScriptPrelude
{
	/// <summary>
	/// 	The names that the prelude declares. A snippet that calls one gets the prelude.
	/// </summary>
	public static readonly string[] HelperNames =
	[
		"OpenOrReuseDocument",
		"CloseDocumentsOpenedHere",
		"ILogicAutomation",
		"ILogicRuleNames",
		"ILogicRuleText",
		"SetILogicRuleText",
		"RunILogicRule",
		"ToInches",
		"FromInches",
		"ToMillimetres",
		"FromMillimetres",
		"ToDegrees",
		"FromDegrees",
		"TryGetUserParameter",
		"StartDeadline"
	];

	private static readonly Lazy<string> _prelude = new(ReadPrelude);

	private static readonly Regex _usesHelper = new(
		$@"\b(?:{string.Join("|", HelperNames)})\s*\(",
		RegexOptions.CultureInvariant | RegexOptions.Compiled);

	/// <summary>
	/// 	True when the snippet calls a helper.
	/// </summary>
	public static bool IsNeeded(string code) => _usesHelper.IsMatch(code);

	/// <summary>
	/// 	Adds the prelude when the snippet calls a helper.
	/// </summary>
	/// <param name="code">
	/// 	The snippet.
	/// </param>
	/// <returns>
	/// 	The code to send.
	/// </returns>
	public static string Apply(string code)
	{
		if (IsNeeded(code) is false)
			return code;

		string[] lines = code.Split('\n');
		int header = 0;

		// Blank lines, comments, #r and using directives at the top stay on top, on their own line numbers.
		while (header < lines.Length && IsHeaderLine(lines[header]))
			header++;

		StringBuilder composed = new();

		for (int index = 0; index < header; index++)
			_ = composed.Append(lines[index].TrimEnd('\r')).Append('\n');

		_ = composed
			.Append("#line hidden\n")
			.Append(_prelude.Value)
			.Append('\n')
			.Append("#line ").Append(header + 1).Append('\n');

		for (int index = header; index < lines.Length; index++)
		{
			_ = composed.Append(lines[index].TrimEnd('\r'));

			if (index < lines.Length - 1)
				_ = composed.Append('\n');
		}

		return composed.ToString();
	}

	private static bool IsHeaderLine(string line)
	{
		string trimmed = line.Trim();

		return trimmed.Length == 0
			|| trimmed.StartsWith("//", StringComparison.Ordinal)
			|| trimmed.StartsWith("#r ", StringComparison.Ordinal)
			|| UsingDirective().IsMatch(trimmed);
	}

	private static string ReadPrelude()
	{
		using Stream stream = typeof(ScriptPrelude).Assembly.GetManifestResourceStream("InventorMcp.Server.ScriptPrelude.csx")
			?? throw new InvalidOperationException("The embedded resource ScriptPrelude.csx is missing.");

		using StreamReader reader = new(stream);

		return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	/// <summary>
	/// 	A using directive, ex. <c>using System.IO;</c>, <c>using static System.Math;</c> or <c>using IO = System.IO;</c>.
	/// 	Not a using statement or declaration (ex. <c>using var x = ...;</c> or <c>using (...)</c>).
	/// </summary>
	[GeneratedRegex(@"^using\s+(?:static\s+)?(?:\w+\s*=\s*)?[A-Za-z_][\w.]*(?:<[\w.,\s<>]+>)?\s*;$", RegexOptions.CultureInvariant)]
	private static partial Regex UsingDirective();
}