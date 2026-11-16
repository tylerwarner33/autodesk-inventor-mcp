using System.Globalization;
using System.Text;

using InventorMcp.Contracts.Models;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	Keeps the text of an execution result to a size that fits in a model's context, and saves the full text to a file.
/// </summary>
/// <remarks>
/// 	A snippet can return or log any amount of text (ex. every parameter of a large assembly), and a client puts all
/// 	of it in the context of the model.
/// 	The limit is in the server, so it needs no rebuild of the add-in.
/// 	The audit log keeps the snippet and one result line, not the output. So a text that is cut goes to a file first,
/// 	and the marker names the file: the model does not run a snippet again to read the rest, which would repeat a write.
/// 	The numbers and the client limits are in <c>Docs/Architecture.md</c>, "Results have a size limit".
/// </remarks>
internal static class OutputLimit
{
	/// <summary>
	/// 	The most characters of <c>Output</c> and <c>ReturnValue</c> together, for a result that succeeded.
	/// </summary>
	/// <remarks>
	/// 	Below the 50,000 characters at which Claude Code moves a text result to a file.
	/// 	<c>inventor_assembly_tree</c> uses the same number.
	/// </remarks>
	public const int MaxCharacters = 40_000;

	/// <summary>
	/// 	The most characters for a result that failed.
	/// </summary>
	/// <remarks>
	/// 	Claude Code keeps only the first and the last 5,000 characters of an error result above about 11,000.
	/// </remarks>
	public const int MaxFailedCharacters = 10_000;

	/// <summary>
	/// 	The most diagnostics that a result holds.
	/// </summary>
	public const int MaxDiagnostics = 50;

	/// <summary>
	/// 	The age after which a file of full text is deleted, the same as the server logs.
	/// </summary>
	public static readonly TimeSpan FileLifetime = TimeSpan.FromDays(14);

	private const double HeadShare = 0.7;

	private const string Separator = "--- return value ---";

	/// <summary>
	/// 	The folder of the files with the full text.
	/// </summary>
	/// <remarks>
	/// 	Settable so that a test writes to a temporary folder, not to the user's folder.
	/// </remarks>
	public static string Folder { get; set; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"InventorMcp",
		"Results");

	/// <summary>
	/// 	Cuts a result that is over the limit to its start and its end, and says so in the result.
	/// </summary>
	/// <param name="execution">
	/// 	The result from the add-in.
	/// </param>
	/// <param name="maxCharacters">
	/// 	The limit for <c>Output</c> and <c>ReturnValue</c> together. Null gives <see cref="MaxCharacters"/>, or
	/// 	<see cref="MaxFailedCharacters"/> for a result that failed.
	/// </param>
	/// <param name="saveFullText">
	/// 	Saves the full text, and returns the path of the file, or <see langword="null"/> when it cannot.
	/// 	Null gives <see cref="SaveFullText"/>.
	/// </param>
	/// <returns>
	/// 	The same instance when the result is inside the limit.
	/// 	Else a result with a marker that gives what was left out and the file with the full text.
	/// </returns>
	public static ExecutionResult Apply(ExecutionResult execution, int? maxCharacters = null, Func<string, string?>? saveFullText = null)
	{
		int limit = maxCharacters ?? (execution.Succeeded ? MaxCharacters : MaxFailedCharacters);

		ExecutionResult current = execution.Diagnostics.Count > MaxDiagnostics
			? execution with { Diagnostics = [.. execution.Diagnostics.Take(MaxDiagnostics), $"[{execution.Diagnostics.Count - MaxDiagnostics} more diagnostics left out.]"] }
			: execution;

		int outputLength = execution.Output.Sum(static line => line.Length + 1);
		int returnLength = execution.ReturnValue?.Length ?? 0;

		if (outputLength + returnLength <= limit)
			return current;

		// A long return value keeps at least half of the limit, and short output leaves the rest to it.
		int returnBudget = Math.Min(returnLength, Math.Max(limit - outputLength, limit / 2));
		int outputBudget = limit - returnBudget;

		string? file = (saveFullText ?? SaveFullText)(FullText(execution));
		string where = file is null ? "The full text could not be saved." : string.Create(CultureInfo.InvariantCulture, $"Full text: {file}");

		return current with
		{
			Output = outputLength > outputBudget ? CutLines(execution.Output, outputBudget, where) : current.Output,
			ReturnValue = returnLength > returnBudget ? CutText(execution.ReturnValue!, returnBudget, where) : current.ReturnValue
		};
	}

	/// <summary>
	/// 	Writes the full text to a file below <see cref="Folder"/>, and deletes the files that are too old.
	/// </summary>
	/// <param name="text">
	/// 	The full text.
	/// </param>
	/// <returns>
	/// 	The path of the file, or <see langword="null"/> when the file could not be written.
	/// </returns>
	public static string? SaveFullText(string text)
	{
		try
		{
			_ = Directory.CreateDirectory(Folder);
			DeleteOldFiles();

			string path = Path.Combine(
				Folder,
				string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.txt"));

			File.WriteAllText(path, text);

			return path;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// The result is still returned, and its marker says that the full text was not saved.
			return null;
		}
	}

	private static void DeleteOldFiles()
	{
		DateTime oldest = DateTime.UtcNow - FileLifetime;

		foreach (string path in Directory.EnumerateFiles(Folder, "*.txt"))
		{
			try
			{
				if (File.GetLastWriteTimeUtc(path) < oldest)
					File.Delete(path);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// A file that cannot be deleted now is deleted by a later call.
			}
		}
	}

	private static string FullText(ExecutionResult execution)
	{
		StringBuilder text = new();

		foreach (string line in execution.Output)
			_ = text.Append(line).Append('\n');

		return text.Append(Separator).Append('\n').Append(execution.ReturnValue).ToString();
	}

	/// <summary>
	/// 	Keeps the first and the last lines that fit the budget, with one marker line between them.
	/// </summary>
	private static List<string> CutLines(IReadOnlyList<string> lines, int budget, string where)
	{
		int headBudget = (int)(budget * HeadShare);
		int tailBudget = budget - headBudget;
		int longest = Math.Max(1, budget / 2);

		// One line that is longer than its share is cut in the same way, so it cannot use the whole budget.
		List<string> shaped = [.. lines.Select(line => line.Length > longest ? CutText(line, longest, where) : line)];

		int first = 0;
		int used = 0;

		while (first < shaped.Count && used + shaped[first].Length + 1 <= headBudget)
		{
			used += shaped[first].Length + 1;
			first++;
		}

		int last = shaped.Count - 1;
		used = 0;

		while (last >= first && used + shaped[last].Length + 1 <= tailBudget)
		{
			used += shaped[last].Length + 1;
			last--;
		}

		if (first > last)
			return shaped;

		int leftOutCharacters = lines.Skip(first).Take(last - first + 1).Sum(static line => line.Length + 1);
		List<string> result = [.. shaped.Take(first)];

		result.Add(string.Create(CultureInfo.InvariantCulture, $"[TRUNCATED: {last - first + 1} lines ({leftOutCharacters} characters) left out. {where}]"));
		result.AddRange(shaped.Skip(last + 1));

		return result;
	}

	/// <summary>
	/// 	Keeps the start and the end of a text, with one marker between them, and never cuts a surrogate pair.
	/// </summary>
	private static string CutText(string text, int budget, string where)
	{
		int head = (int)(budget * HeadShare);
		int tail = budget - head;

		if (head > 0 && char.IsHighSurrogate(text[head - 1]))
			head--;

		if (tail > 0 && char.IsLowSurrogate(text[text.Length - tail]))
			tail--;

		return string.Create(
			CultureInfo.InvariantCulture,
			$"{text[..head]}\n[TRUNCATED: {text.Length - head - tail} characters left out. {where}]\n{text[^tail..]}");
	}
}
