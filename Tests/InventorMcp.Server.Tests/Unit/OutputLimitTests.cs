using System.Text;

using InventorMcp.Contracts.Models;
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

		ExecutionResult limited = OutputLimit.Apply(result, Limit, _ =>
		{
			saved = true;

			return "file";
		});

		Assert.Same(result, limited);
		Assert.False(saved);
	}

	[Fact]
	public void OutputOverTheLimitKeepsTheFirstAndTheLastLineWithOneMarker()
	{
		string[] lines = [.. Enumerable.Range(1, 500).Select(static index => $"line {index:D4}")];
		string? savedText = null;

		ExecutionResult limited = OutputLimit.Apply(Result(output: lines), Limit, text =>
		{
			savedText = text;

			return "C:\\Results\\full.txt";
		});

		string marker = Assert.Single(limited.Output, static line => line.StartsWith('['));

		Assert.Equal("line 0001", limited.Output[0]);
		Assert.Equal("line 0500", limited.Output[^1]);
		Assert.Matches(@"^\[TRUNCATED: \d+ lines \(\d+ characters\) left out\. Full text: C:\\Results\\full\.txt\]$", marker);
		Assert.InRange(limited.Output.Sum(static line => line.Length + 1), 1, Limit + marker.Length + 1);
		Assert.Contains("line 0250", savedText, StringComparison.Ordinal);
	}

	[Fact]
	public void TheMarkerCountsTheLinesAndCharactersThatWereLeftOut()
	{
		string[] lines = [.. Enumerable.Range(1, 500).Select(static index => $"line {index:D4}")];

		ExecutionResult limited = OutputLimit.Apply(Result(output: lines), Limit, static _ => "f");

		int kept = limited.Output.Count(static line => !line.StartsWith('['));
		string marker = limited.Output.Single(static line => line.StartsWith('['));

		Assert.StartsWith($"[TRUNCATED: {500 - kept} lines ({(500 - kept) * 10} characters) left out.", marker, StringComparison.Ordinal);
	}

	[Fact]
	public void OneLineLongerThanTheLimitIsCut()
	{
		ExecutionResult limited = OutputLimit.Apply(Result(output: [new string('x', 50_000)]), Limit, static _ => "f");

		string line = Assert.Single(limited.Output);

		Assert.InRange(line.Length, 1, Limit + 100);
		Assert.Contains("characters left out", line, StringComparison.Ordinal);
	}

	[Fact]
	public void ALongReturnValueKeepsItsStartAndItsEnd()
	{
		string returnValue = new string('a', 20_000) + new string('z', 20_000);

		ExecutionResult limited = OutputLimit.Apply(Result(output: ["short"], returnValue: returnValue), Limit, static _ => "f");

		Assert.InRange(limited.ReturnValue!.Length, 1, Limit + 100);
		Assert.StartsWith("aaa", limited.ReturnValue, StringComparison.Ordinal);
		Assert.EndsWith("zzz", limited.ReturnValue, StringComparison.Ordinal);
		Assert.Contains("[TRUNCATED:", limited.ReturnValue, StringComparison.Ordinal);
		Assert.Equal(["short"], limited.Output);
	}

	[Fact]
	public void AFailedResultUsesTheSmallerLimit()
	{
		string[] lines = [.. Enumerable.Range(1, 3_000).Select(static index => $"line {index:D5}")];

		ExecutionResult failed = OutputLimit.Apply(Result(output: lines) with { Succeeded = false }, saveFullText: static _ => "f");
		ExecutionResult succeeded = OutputLimit.Apply(Result(output: lines), saveFullText: static _ => "f");

		Assert.True(Length(failed) <= OutputLimit.MaxFailedCharacters + 200);
		Assert.True(Length(succeeded) > OutputLimit.MaxFailedCharacters + 200);
		Assert.True(Length(succeeded) <= OutputLimit.MaxCharacters + 200);
	}

	[Fact]
	public void ASaveThatFailsIsNamedInTheMarker()
	{
		string[] lines = [.. Enumerable.Range(1, 500).Select(static index => $"line {index:D4}")];

		ExecutionResult limited = OutputLimit.Apply(Result(output: lines), Limit, static _ => null);

		Assert.Contains("The full text could not be saved.", limited.Output.Single(static line => line.StartsWith('[')), StringComparison.Ordinal);
	}

	[Fact]
	public void MoreThanFiftyDiagnosticsAreCutWithACount()
	{
		ExecutionResult result = Result(output: []) with { Diagnostics = [.. Enumerable.Range(1, 80).Select(static index => $"CS{index}")] };

		ExecutionResult limited = OutputLimit.Apply(result, Limit, static _ => "f");

		Assert.Equal(OutputLimit.MaxDiagnostics + 1, limited.Diagnostics.Count);
		Assert.Equal("[30 more diagnostics left out.]", limited.Diagnostics[^1]);
	}

	[Fact]
	public void TheCutNeverSplitsASurrogatePair()
	{
		// With a budget of 1,000 the start is 700 characters and the end is 300. The first emoji has its first half at
		// index 699, the last character of the start, and the second has its second half 300 characters from the end.
		string returnValue = new string('a', 699) + "\U0001F600" + new string('b', 1_000) + "\U0001F600" + new string('c', 299);

		ExecutionResult limited = OutputLimit.Apply(Result(output: [], returnValue: returnValue), Limit, static _ => "f");

		Assert.True(IsWellFormed(limited.ReturnValue!));
	}

	[Fact]
	public void TheFileHasTheFullTextAndAnOldFileIsDeleted()
	{
		OutputLimit.Folder = _folder;

		try
		{
			_ = Directory.CreateDirectory(_folder);
			string oldFile = Path.Combine(_folder, "20200101-000000-aaaaaaaa.txt");
			File.WriteAllText(oldFile, "old");
			File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow - OutputLimit.FileLifetime - TimeSpan.FromDays(1));

			string[] lines = [.. Enumerable.Range(1, 500).Select(static index => $"line {index:D4}")];
			ExecutionResult limited = OutputLimit.Apply(Result(output: lines, returnValue: "the value"), Limit);

			string marker = limited.Output.Single(static line => line.StartsWith('['));
			string file = marker[(marker.IndexOf("Full text: ", StringComparison.Ordinal) + "Full text: ".Length)..^1];
			string text = File.ReadAllText(file);

			Assert.StartsWith(_folder, file, StringComparison.Ordinal);
			Assert.Contains("line 0250", text, StringComparison.Ordinal);
			Assert.EndsWith("the value", text, StringComparison.Ordinal);
			Assert.False(File.Exists(oldFile));
		}
		finally
		{
			OutputLimit.Folder = Path.Combine(Path.GetTempPath(), "InventorMcp.Tests", "Results-unused");
		}
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
