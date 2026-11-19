using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

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
/// 	The limit counts the characters of the JSON text that the client gets, after escaping, because that is what the
/// 	client measures.
/// 	The numbers and the client limits are in <c>Docs/Architecture.md</c>, "Results have a size limit".
/// </remarks>
internal static class OutputLimit
{
	/// <summary>
	/// 	The most JSON characters of a result that succeeded.
	/// </summary>
	/// <remarks>
	/// 	Below the 50,000 characters at which Claude Code moves a text result to a file.
	/// 	<c>inventor_assembly_tree</c> uses the same number.
	/// </remarks>
	public const int MaxCharacters = 40_000;

	/// <summary>
	/// 	The most JSON characters of a result that failed.
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

	/// <summary>
	/// 	The folder of the files with the full text.
	/// </summary>
	public static readonly string Folder = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"InventorMcp",
		"Results");

	private const double HeadShare = 0.7;

	/// <summary>
	/// 	The JSON characters kept free for the member names, the numbers, the truncation members and the long execution
	/// 	warning, which the server adds after the limit.
	/// </summary>
	private const int MaxReserve = 1_000;

	/// <summary>
	/// 	The marker of a cut text is not in its budget, so a line that is cut is this much shorter than its share.
	/// </summary>
	private const int MarkerReserve = 200;

	private const string Separator = "--- return value ---";

	/// <summary>
	/// 	The encoder of the tool results. See <c>ToolPipeline.ResultSerializerOptions</c>.
	/// </summary>
	private static readonly JavaScriptEncoder _encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

	/// <summary>
	/// 	The JSON characters of one half of a surrogate pair. The encoder escapes a character above U+FFFF as two
	/// 	<c>\uXXXX</c> sequences.
	/// </summary>
	private static readonly int _surrogateLength = _encoder.WillEncode(0x1F600) ? 6 : 1;

	/// <summary>
	/// 	Cuts a result that is over the limit to its start and its end, and says so in the result.
	/// </summary>
	/// <param name="execution">
	/// 	The result from the add-in.
	/// </param>
	/// <param name="maxCharacters">
	/// 	The limit for the JSON text of the result. Null gives <see cref="MaxCharacters"/>, or
	/// 	<see cref="MaxFailedCharacters"/> for a result that failed.
	/// </param>
	/// <param name="saveFullText">
	/// 	Saves the full text, and returns the path of the file, or <see langword="null"/> when it cannot.
	/// 	Null gives <see cref="SaveFullText(string)"/>.
	/// </param>
	/// <returns>
	/// 	The same instance and no truncation when the result is inside the limit.
	/// 	Else a result with a marker that gives what was left out and the file with the full text.
	/// </returns>
	public static LimitedExecution Apply(ExecutionResult execution, int? maxCharacters = null, Func<string, string?>? saveFullText = null)
	{
		int limit = maxCharacters ?? (execution.Succeeded ? MaxCharacters : MaxFailedCharacters);
		int share = Math.Max(1, limit / 4);

		ExecutionResult current = LimitDiagnostics(execution, share);

		if (current.ExceptionMessage is { } exceptionMessage && JsonLength(exceptionMessage) > share)
			current = current with { ExceptionMessage = CutText(exceptionMessage, share, "The message is longer than the limit.") };

		int fixedLength = Math.Min(MaxReserve, limit / 10)
			+ JsonLength(current.ExceptionType)
			+ JsonLength(current.ExceptionMessage)
			+ JsonLength(current.ReturnType)
			+ current.Diagnostics.Sum(static line => JsonLength(line) + 3)
			+ (current.DocumentsLeftOpen?.Sum(static path => JsonLength(path) + 3) ?? 0);

		int available = Math.Max(share, limit - fixedLength);
		int outputLength = execution.Output.Sum(static line => JsonLength(line) + 3);
		int returnLength = execution.ReturnValue is null ? 0 : JsonLength(execution.ReturnValue) + 2;

		if (outputLength + returnLength <= available)
			return new LimitedExecution(current, OutputTruncated: false, ReturnValueTruncated: false, FullTextFile: null);

		// A long return value keeps at least half of the limit, and short output leaves the rest to it.
		int returnBudget = Math.Min(returnLength, Math.Max(available - outputLength, available / 2));
		int outputBudget = available - returnBudget;

		string? file = (saveFullText ?? SaveFullText)(FullText(execution));
		string where = file is null ? "The full text could not be saved." : string.Create(CultureInfo.InvariantCulture, $"Full text: {file}");

		bool outputTruncated = outputLength > outputBudget;
		bool returnValueTruncated = returnLength > returnBudget;

		return new LimitedExecution(
			current with
			{
				Output = outputTruncated ? CutLines(execution.Output, outputBudget, where) : current.Output,
				ReturnValue = returnValueTruncated ? CutText(execution.ReturnValue!, Math.Max(1, returnBudget - 2), where) : current.ReturnValue
			},
			outputTruncated,
			returnValueTruncated,
			file);
	}

	/// <summary>
	/// 	The characters of a string in the JSON text of a tool result, without its quotes.
	/// </summary>
	/// <param name="text">
	/// 	The string, or null.
	/// </param>
	/// <returns>
	/// 	The length after escaping, 0 for null.
	/// </returns>
	public static int JsonLength(string? text)
	{
		if (text is null)
			return 0;

		int length = 0;

		foreach (char character in text)
			length += JsonLength(character);

		return length;
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
	public static string? SaveFullText(string text) => SaveFullText(text, Folder);

	/// <summary>
	/// 	Writes the full text to a file below a folder, and deletes the files that are too old.
	/// </summary>
	/// <param name="text">
	/// 	The full text.
	/// </param>
	/// <param name="folder">
	/// 	The folder. A test gives a temporary folder.
	/// </param>
	/// <returns>
	/// 	The path of the file, or <see langword="null"/> when the file could not be written.
	/// </returns>
	public static string? SaveFullText(string text, string folder)
	{
		try
		{
			_ = Directory.CreateDirectory(folder);
			DeleteOldFiles(folder);

			string path = Path.Combine(
				folder,
				string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.txt"));

			File.WriteAllText(path, text);

			return path;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
		{
			// The result is still returned, and its marker says that the full text was not saved.
			return null;
		}
	}

	private static int JsonLength(char character)
	{
		if (char.IsSurrogate(character))
			return _surrogateLength;

		if (_encoder.WillEncode(character) is false)
			return 1;

		return character is '"' or '\\' or '\b' or '\f' or '\n' or '\r' or '\t' ? 2 : 6;
	}

	private static ExecutionResult LimitDiagnostics(ExecutionResult execution, int budget)
	{
		int kept = 0;
		int used = 0;

		while (kept < execution.Diagnostics.Count && kept < MaxDiagnostics && used + JsonLength(execution.Diagnostics[kept]) + 3 <= budget)
		{
			used += JsonLength(execution.Diagnostics[kept]) + 3;
			kept++;
		}

		// The first diagnostic stays, also when it is longer than the budget, because it usually gives the cause.
		if (kept == 0 && execution.Diagnostics.Count > 0)
			return execution with { Diagnostics = [CutText(execution.Diagnostics[0], budget, "The diagnostic is longer than the limit."), .. LeftOut(execution.Diagnostics.Count - 1)] };

		return kept == execution.Diagnostics.Count
			? execution
			: execution with { Diagnostics = [.. execution.Diagnostics.Take(kept), .. LeftOut(execution.Diagnostics.Count - kept)] };
	}

	private static string[] LeftOut(int count) =>
		count == 0 ? [] : [string.Create(CultureInfo.InvariantCulture, $"[{count} more diagnostics left out.]")];

	private static void DeleteOldFiles(string folder)
	{
		DateTime oldest = DateTime.UtcNow - FileLifetime;

		foreach (string path in Directory.EnumerateFiles(folder, "*.txt"))
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

		// A line longer than the smaller share is cut to it, so the last line, which often has the summary, always keeps
		// its end.
		int longest = Math.Max(1, tailBudget - MarkerReserve);
		List<string> shaped = [.. lines.Select(line => JsonLength(line) > longest ? CutText(line, longest, where) : line)];

		int first = 0;
		int used = 0;

		while (first < shaped.Count && used + JsonLength(shaped[first]) + 3 <= headBudget)
		{
			used += JsonLength(shaped[first]) + 3;
			first++;
		}

		int last = shaped.Count - 1;
		used = 0;

		while (last >= first && used + JsonLength(shaped[last]) + 3 <= tailBudget)
		{
			used += JsonLength(shaped[last]) + 3;
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
	/// 	Keeps the start and the end of a text in a budget of JSON characters, with one marker between them, and never
	/// 	cuts a surrogate pair.
	/// </summary>
	private static string CutText(string text, int budget, string where)
	{
		int headBudget = (int)(budget * HeadShare);
		int head = 0;
		int used = 0;

		while (head < text.Length && used + JsonLength(text[head]) <= headBudget)
			used += JsonLength(text[head++]);

		if (head > 0 && char.IsHighSurrogate(text[head - 1]))
			head--;

		int tailBudget = budget - used;
		int tail = 0;
		used = 0;

		while (tail < text.Length - head && used + JsonLength(text[text.Length - 1 - tail]) <= tailBudget)
			used += JsonLength(text[text.Length - 1 - tail++]);

		if (tail > 0 && char.IsLowSurrogate(text[text.Length - tail]))
			tail--;

		if (head + tail >= text.Length)
			return text;

		return string.Create(
			CultureInfo.InvariantCulture,
			$"{text[..head]}\n[TRUNCATED: {text.Length - head - tail} characters left out. {where}]\n{text[(text.Length - tail)..]}");
	}
}

/// <summary>
/// 	An execution result after the size limit, and what the limit cut.
/// </summary>
/// <param name="Result">
/// 	The result, changed or not.
/// </param>
/// <param name="OutputTruncated">
/// 	True when output lines were left out.
/// </param>
/// <param name="ReturnValueTruncated">
/// 	True when the return value was cut, so it does not parse as JSON.
/// </param>
/// <param name="FullTextFile">
/// 	The file with the full text, or null when nothing was cut or the file could not be written.
/// </param>
internal readonly record struct LimitedExecution(ExecutionResult Result, bool OutputTruncated, bool ReturnValueTruncated, string? FullTextFile)
{
	/// <summary>
	/// 	True when the limit cut the output or the return value.
	/// </summary>
	public bool Truncated => OutputTruncated || ReturnValueTruncated;

	/// <summary>
	/// 	The value that the tool returns.
	/// </summary>
	/// <returns>
	/// 	The execution result when nothing was cut, else the same members and the truncation members.
	/// </returns>
	public object ToToolResult() => Truncated ? new TruncatedExecutionResult(this) : Result;
}

/// <summary>
/// 	An execution result that the size limit cut, as the tool returns it.
/// </summary>
/// <remarks>
/// 	<see cref="ExecutionResult"/> is shared with the add-in, so the truncation members are here and not on it.
/// 	The members have the same names, so a model reads one shape.
/// </remarks>
/// <param name="limited">
/// 	The result after the limit.
/// </param>
internal sealed class TruncatedExecutionResult(LimitedExecution limited)
{
	/// <summary>
	/// 	The result after the limit, for the server. Not serialized.
	/// </summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public ExecutionResult Execution => limited.Result;

	/// <inheritdoc cref="ExecutionResult.Succeeded"/>
	public bool Succeeded => limited.Result.Succeeded;

	/// <inheritdoc cref="ExecutionResult.ReturnValue"/>
	public string? ReturnValue => limited.Result.ReturnValue;

	/// <inheritdoc cref="ExecutionResult.ReturnType"/>
	public string? ReturnType => limited.Result.ReturnType;

	/// <inheritdoc cref="ExecutionResult.Output"/>
	public IReadOnlyList<string> Output => limited.Result.Output;

	/// <inheritdoc cref="ExecutionResult.Diagnostics"/>
	public IReadOnlyList<string> Diagnostics => limited.Result.Diagnostics;

	/// <inheritdoc cref="ExecutionResult.ExceptionType"/>
	public string? ExceptionType => limited.Result.ExceptionType;

	/// <inheritdoc cref="ExecutionResult.ExceptionMessage"/>
	public string? ExceptionMessage => limited.Result.ExceptionMessage;

	/// <inheritdoc cref="ExecutionResult.ElapsedMilliseconds"/>
	public long ElapsedMilliseconds => limited.Result.ElapsedMilliseconds;

	/// <inheritdoc cref="ExecutionResult.WallClockMilliseconds"/>
	public long? WallClockMilliseconds => limited.Result.WallClockMilliseconds;

	/// <inheritdoc cref="ExecutionResult.DocumentsLeftOpen"/>
	public IReadOnlyList<string>? DocumentsLeftOpen => limited.Result.DocumentsLeftOpen;

	/// <inheritdoc cref="LimitedExecution.OutputTruncated"/>
	public bool OutputTruncated => limited.OutputTruncated;

	/// <inheritdoc cref="LimitedExecution.ReturnValueTruncated"/>
	public bool ReturnValueTruncated => limited.ReturnValueTruncated;

	/// <inheritdoc cref="LimitedExecution.FullTextFile"/>
	public string? FullTextFile => limited.FullTextFile;
}
