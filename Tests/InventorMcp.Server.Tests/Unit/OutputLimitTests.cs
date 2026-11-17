using System.Text;
using System.Text.Json;

using InventorMcp.Contracts.Models;
using InventorMcp.Server.McpTools;
using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The size limit of an execution result, and the file with the full text.
/// </summary>
[Trait("Level", "Unit")]
public sealed class OutputLimitTests : IDisposable
{
	private const int Limit = 1_000;

	private readonly string _folder = Path.Combine(Path.GetTempPath(), "InventorMcp.Tests", "Results-" + Guid.NewGuid().ToString("N"));

	[Fact]
	public void ATextUnderTheLimitIsTheSameInstanceAndSavesNothing()
	{
		ExecutionResult result = Result(output: ["one", "two"], returnValue: "3");
		bool saved = false;

		LimitedExecution limited = OutputLimit.Apply(result, Limit, _ =>
		{
			saved = true;

			return "file";
		});

		Assert.Same(result, limited.Result);
		Assert.False(limited.Truncated);
		Assert.Same(result, limited.ToToolResult());
		Assert.False(saved);
	}

	[Fact]
	public void OutputOverTheLimitKeepsTheFirstAndTheLastLineWithOneMarker()
	{
		string[] lines = [.. Enumerable.Range(1, 500).Select(static index => $"line {index:D4}")];
		string? savedText = null;

		LimitedExecution limited = OutputLimit.Apply(Result(output: lines), Limit, text =>
		{
			savedText = text;

			return "C:\\Results\\full.txt";
		});

		IReadOnlyList<string> output = limited.Result.Output;
		string marker = Assert.Single(output, static line => line.StartsWith('['));

		Assert.Equal("line 0001", output[0]);
		Assert.Equal("line 0500", output[^1]);
		Assert.Matches(@"^\[TRUNCATED: \d+ lines \(\d+ characters\) left out\. Full text: C:\\Results\\full\.txt\]$", marker);
		Assert.InRange(output.Sum(static line => line.Length + 1), 1, Limit + marker.Length + 1);
		Assert.Contains("line 0250", savedText, StringComparison.Ordinal);
		Assert.True(limited.OutputTruncated);
		Assert.False(limited.ReturnValueTruncated);
		Assert.Equal("C:\\Results\\full.txt", limited.FullTextFile);
	}

	[Fact]
	public void TheMarkerCountsTheLinesAndCharactersThatWereLeftOut()
	{
		string[] lines = [.. Enumerable.Range(1, 500).Select(static index => $"line {index:D4}")];

		IReadOnlyList<string> output = OutputLimit.Apply(Result(output: lines), Limit, static _ => "f").Result.Output;

		int kept = output.Count(static line => !line.StartsWith('['));
		string marker = output.Single(static line => line.StartsWith('['));

		Assert.StartsWith($"[TRUNCATED: {500 - kept} lines ({(500 - kept) * 10} characters) left out.", marker, StringComparison.Ordinal);
	}

	[Fact]
	public void OneLineLongerThanTheLimitIsCut()
	{
		LimitedExecution limited = OutputLimit.Apply(Result(output: [new string('x', 50_000)]), Limit, static _ => "f");

		string line = Assert.Single(limited.Result.Output);

		Assert.InRange(line.Length, 1, Limit + 100);
		Assert.Contains("characters left out", line, StringComparison.Ordinal);
	}

	[Fact]
	public void ALongLastLineKeepsItsEnd()
	{
		// The last line is between the tail share and half of the budget, the case that a cut of whole lines drops.
		string[] lines = [.. Enumerable.Range(1, 200).Select(static index => $"line {index:D4}"), "summary " + new string('s', 400) + " END"];

		IReadOnlyList<string> output = OutputLimit.Apply(Result(output: lines), Limit, static _ => "f").Result.Output;

		Assert.EndsWith(" END", output[^1], StringComparison.Ordinal);
		Assert.Equal("line 0001", output[0]);
	}

	[Fact]
	public void ALongReturnValueKeepsItsStartAndItsEnd()
	{
		string returnValue = new string('a', 20_000) + new string('z', 20_000);

		LimitedExecution limited = OutputLimit.Apply(Result(output: ["short"], returnValue: returnValue), Limit, static _ => "f");

		Assert.InRange(limited.Result.ReturnValue!.Length, 1, Limit + 100);
		Assert.StartsWith("aaa", limited.Result.ReturnValue, StringComparison.Ordinal);
		Assert.EndsWith("zzz", limited.Result.ReturnValue, StringComparison.Ordinal);
		Assert.Contains("[TRUNCATED:", limited.Result.ReturnValue, StringComparison.Ordinal);
		Assert.Equal(["short"], limited.Result.Output);
		Assert.True(limited.ReturnValueTruncated);
		Assert.False(limited.OutputTruncated);
	}

	[Fact]
	public void ACutResultHasTheTruncationMembersInItsJson()
	{
		LimitedExecution limited = OutputLimit.Apply(Result(output: [], returnValue: new string('a', 5_000)), Limit, static _ => "C:\\Results\\full.txt");

		using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(limited.ToToolResult(), ToolPipeline.ResultSerializerOptions));
		JsonElement root = json.RootElement;

		Assert.True(root.GetProperty("returnValueTruncated").GetBoolean());
		Assert.False(root.GetProperty("outputTruncated").GetBoolean());
		Assert.Equal("C:\\Results\\full.txt", root.GetProperty("fullTextFile").GetString());
		Assert.True(root.GetProperty("succeeded").GetBoolean());
		Assert.False(root.TryGetProperty("execution", out _));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void TheJsonOfAResultWithQuotesStaysInsideTheLimit(bool succeeded)
	{
		// A JSON return value: each quote is two characters in the text of the result.
		string returnValue = "[" + string.Join(',', Enumerable.Range(1, 5_000).Select(static index => $"{{\"name\":\"d{index}\",\"value\":\"{index}\"}}")) + "]";
		string[] lines = [.. Enumerable.Range(1, 2_000).Select(static index => $"\"quoted\" line {index}\t\\")];
		ExecutionResult result = Result(output: lines, returnValue: returnValue) with
		{
			Succeeded = succeeded,
			Diagnostics = [.. Enumerable.Range(1, 80).Select(static index => $"(1,{index}): error CS0103: The name \"x{index}\" does not exist in the current context")],
			ExceptionMessage = succeeded ? null : new string('m', 20_000)
		};

		LimitedExecution limited = OutputLimit.Apply(result, saveFullText: static _ => "C:\\Results\\full.txt");
		int limit = succeeded ? OutputLimit.MaxCharacters : OutputLimit.MaxFailedCharacters;

		Assert.InRange(JsonSerializer.Serialize(limited.ToToolResult(), ToolPipeline.ResultSerializerOptions).Length, 1, limit);
	}

	[Fact]
	public void JsonLengthCountsTheEscapes()
	{
		const string Mixed = "\"x\" <é> \\ \u0001 \U0001F600";

		Assert.Equal(0, OutputLimit.JsonLength(null));
		Assert.Equal(3, OutputLimit.JsonLength("abc"));
		Assert.Equal(4, OutputLimit.JsonLength("a\"b"));
		Assert.Equal(4, OutputLimit.JsonLength("a\nb"));
		Assert.Equal(3, OutputLimit.JsonLength("a<é"));
		Assert.Equal(JsonSerializer.Serialize(Mixed, ToolPipeline.ResultSerializerOptions).Length - 2, OutputLimit.JsonLength(Mixed));
	}

	[Fact]
	public void AFailedResultUsesTheSmallerLimit()
	{
		string[] lines = [.. Enumerable.Range(1, 3_000).Select(static index => $"line {index:D5}")];

		ExecutionResult failed = OutputLimit.Apply(Result(output: lines) with { Succeeded = false }, saveFullText: static _ => "f").Result;
		ExecutionResult succeeded = OutputLimit.Apply(Result(output: lines), saveFullText: static _ => "f").Result;

		Assert.True(Length(failed) <= OutputLimit.MaxFailedCharacters + 200);
		Assert.True(Length(succeeded) > OutputLimit.MaxFailedCharacters + 200);
		Assert.True(Length(succeeded) <= OutputLimit.MaxCharacters + 200);
	}

	[Fact]
	public void ASaveThatFailsIsNamedInTheMarker()
	{
		string[] lines = [.. Enumerable.Range(1, 500).Select(static index => $"line {index:D4}")];

		LimitedExecution limited = OutputLimit.Apply(Result(output: lines), Limit, static _ => null);

		Assert.Contains("The full text could not be saved.", limited.Result.Output.Single(static line => line.StartsWith('[')), StringComparison.Ordinal);
		Assert.Null(limited.FullTextFile);
	}

	[Fact]
	public void MoreThanFiftyDiagnosticsAreCutWithACount()
	{
		ExecutionResult result = Result(output: []) with { Diagnostics = [.. Enumerable.Range(1, 80).Select(static index => $"CS{index}")] };

		ExecutionResult limited = OutputLimit.Apply(result, OutputLimit.MaxCharacters, static _ => "f").Result;

		Assert.Equal(OutputLimit.MaxDiagnostics + 1, limited.Diagnostics.Count);
		Assert.Equal("[30 more diagnostics left out.]", limited.Diagnostics[^1]);
	}

	[Fact]
	public void LongDiagnosticsKeepAQuarterOfTheLimit()
	{
		ExecutionResult result = Result(output: []) with { Diagnostics = [.. Enumerable.Range(1, 40).Select(static index => $"CS{index}: " + new string('d', 100))] };

		ExecutionResult limited = OutputLimit.Apply(result, Limit, static _ => "f").Result;

		Assert.InRange(limited.Diagnostics.Count, 2, 10);
		Assert.StartsWith("CS1: ", limited.Diagnostics[0], StringComparison.Ordinal);
		Assert.EndsWith("more diagnostics left out.]", limited.Diagnostics[^1], StringComparison.Ordinal);
	}

	[Fact]
	public void TheCutNeverSplitsASurrogatePair()
	{
		// Emojis at each place near the two cuts: for one of the offsets, an emoji falls on each cut.
		StringBuilder returnValue = new();

		for (int index = 0; index < 400; index++)
			_ = returnValue.Append(index % 3 == 0 ? "\U0001F600" : "a");

		for (int offset = 0; offset < 12; offset++)
		{
			ExecutionResult result = Result(output: [], returnValue: new string('b', offset) + returnValue);

			Assert.True(IsWellFormed(OutputLimit.Apply(result, Limit, static _ => "f").Result.ReturnValue!));
		}
	}

	[Fact]
	public void TheLongExecutionWarningIsTheLastLine()
	{
		string[] lines = [.. Enumerable.Range(1, 20_000).Select(static index => $"line {index:D5}")];
		ExecutionResult result = Result(output: lines) with { ElapsedMilliseconds = 30_000 };

		TruncatedExecutionResult completed = Assert.IsType<TruncatedExecutionResult>(InventorTool.Complete(result, 31_000, warnOnLongExecution: true));

		Assert.StartsWith("WARNING:", completed.Output[^1], StringComparison.Ordinal);
		Assert.Equal(31_000, completed.WallClockMilliseconds);
		Assert.InRange(JsonSerializer.Serialize(completed, ToolPipeline.ResultSerializerOptions).Length, 1, OutputLimit.MaxCharacters);
	}

	[Fact]
	public void TheFileHasTheFullTextAndAnOldFileIsDeleted()
	{
		_ = Directory.CreateDirectory(_folder);
		string oldFile = Path.Combine(_folder, "20200101-000000-aaaaaaaa.txt");
		File.WriteAllText(oldFile, "old");
		File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow - OutputLimit.FileLifetime - TimeSpan.FromDays(1));

		string[] lines = [.. Enumerable.Range(1, 500).Select(static index => $"line {index:D4}")];
		LimitedExecution limited = OutputLimit.Apply(Result(output: lines, returnValue: "the value"), Limit, text => OutputLimit.SaveFullText(text, _folder));

		string file = limited.FullTextFile!;
		string text = File.ReadAllText(file);

		Assert.StartsWith(_folder, file, StringComparison.Ordinal);
		Assert.Contains($"Full text: {file}", limited.Result.Output.Single(static line => line.StartsWith('[')), StringComparison.Ordinal);
		Assert.Contains("line 0250", text, StringComparison.Ordinal);
		Assert.EndsWith("the value", text, StringComparison.Ordinal);
		Assert.False(File.Exists(oldFile));
	}

	public void Dispose()
	{
		if (Directory.Exists(_folder))
			Directory.Delete(_folder, recursive: true);
	}

	private static ExecutionResult Result(string[] output, string? returnValue = null) =>
		new(true, returnValue, returnValue is null ? null : "String", output, [], null, null, 5);

	private static int Length(ExecutionResult result) => result.Output.Sum(static line => line.Length + 1);

	/// <summary>
	/// 	True when the text has no half of a surrogate pair, which a strict encoder refuses.
	/// </summary>
	private static bool IsWellFormed(string text)
	{
		try
		{
			_ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(text);

			return true;
		}
		catch (EncoderFallbackException)
		{
			return false;
		}
	}
}
