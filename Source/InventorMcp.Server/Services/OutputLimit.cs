using System.Globalization;

using InventorMcp.Contracts.Models;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	Keeps the text of an execution result to a size that fits in a model's context.
/// </summary>
/// <remarks>
/// 	A snippet can return or log any amount of text (ex. every parameter of a large assembly), and a client puts all
/// 	of it in the context of the model.
/// 	The limit is in the server, so it needs no rebuild of the add-in.
/// </remarks>
internal static class OutputLimit
{
	/// <summary>
	/// 	The most characters of a return value, and of all the output lines together.
	/// </summary>
	/// <remarks>
	/// 	The same size as the limit of <c>inventor_assembly_tree</c>.
	/// </remarks>
	public const int MaxCharacters = 60_000;

	/// <summary>
	/// 	Cuts a return value and the output lines that are over the limit, and says so in the result.
	/// </summary>
	/// <param name="execution">
	/// 	The result from the add-in.
	/// </param>
	/// <param name="maxCharacters">
	/// 	The limit, for a test.
	/// </param>
	/// <returns>
	/// 	The same result when it is inside the limit.
	/// </returns>
	public static ExecutionResult Apply(ExecutionResult execution, int maxCharacters = MaxCharacters)
	{
		string? returnValue = execution.ReturnValue;
		bool returnValueCut = returnValue is not null && returnValue.Length > maxCharacters;

		if (returnValueCut)
			returnValue = Cut(returnValue!, maxCharacters);

		List<string> output = [];
		int used = 0;
		int linesLeftOut = 0;

		foreach (string line in execution.Output)
		{
			int left = maxCharacters - used;
			used += line.Length;

			if (left <= 0)
				linesLeftOut++;
			else
				output.Add(line.Length > left ? Cut(line, left) : line);
		}

		if (returnValueCut is false && used <= maxCharacters)
			return execution;

		if (returnValueCut)
		{
			output.Add(string.Create(
				CultureInfo.InvariantCulture,
				$"TRUNCATED: the return value had {execution.ReturnValue!.Length} characters, and the first {maxCharacters} are here. Return less (ex. a count, a filter or one page), or write the full text to a file and return its path."));
		}

		if (used > maxCharacters)
		{
			output.Add(string.Create(
				CultureInfo.InvariantCulture,
				$"TRUNCATED: the output had {used} characters in {execution.Output.Count} lines, and the first {maxCharacters} characters are here ({linesLeftOut} lines left out). Log less, or write the full text to a file."));
		}

		return execution with { ReturnValue = returnValue, Output = output };
	}

	/// <summary>
	/// 	Cuts text at a length, and never between the two halves of a surrogate pair.
	/// </summary>
	private static string Cut(string text, int length) =>
		length > 0 && char.IsHighSurrogate(text[length - 1]) ? text[..(length - 1)] : text[..length];
}
