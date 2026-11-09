using InventorMcp.AddIn;
using InventorMcp.AddIn.Operations;
using InventorMcp.Contracts.Models;
using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The add-in's rolling log file and audit text, compiled into the tests from the add-in's source, and the
/// 	server's limit on the text of an execution result.
/// </summary>
[Trait("Level", "Unit")]
public sealed class AddInLogTests
{
	private static readonly DateTimeOffset _first = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

	[Fact]
	public void FullFileMovesUpAndTheOldestIsDeleted()
	{
		string folder = Path.Combine(Path.GetTempPath(), $"InventorMcp.Tests.{Guid.NewGuid():N}");
		string path = Path.Combine(folder, "executed-code.log");
		_ = Directory.CreateDirectory(folder);

		try
		{
			// Each line fills the file, so each later append starts a new file.
			Assert.False(RollingFile.Append(path, "one\n", maxBytes: 4, keep: 2));
			Assert.True(RollingFile.Append(path, "two\n", maxBytes: 4, keep: 2));
			Assert.True(RollingFile.Append(path, "three\n", maxBytes: 4, keep: 2));
			Assert.True(RollingFile.Append(path, "four\n", maxBytes: 4, keep: 2));

			Assert.Equal("four\n", File.ReadAllText(path));
			Assert.Equal("three\n", File.ReadAllText(Path.Combine(folder, "executed-code.1.log")));
			Assert.Equal("two\n", File.ReadAllText(Path.Combine(folder, "executed-code.2.log")));
			Assert.False(File.Exists(Path.Combine(folder, "executed-code.3.log")));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Fact]
	public void FileUnderTheLimitIsAppendedTo()
	{
		string folder = Path.Combine(Path.GetTempPath(), $"InventorMcp.Tests.{Guid.NewGuid():N}");
		string path = Path.Combine(folder, "addin.log");
		_ = Directory.CreateDirectory(folder);

		try
		{
			Assert.False(RollingFile.Append(path, "one\n"));
			Assert.False(RollingFile.Append(path, "two\n"));

			Assert.Equal("one\ntwo\n", File.ReadAllText(path));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Fact]
	public void FileThatAnotherProcessHoldsStillGetsTheText()
	{
		string folder = Path.Combine(Path.GetTempPath(), $"InventorMcp.Tests.{Guid.NewGuid():N}");
		string path = Path.Combine(folder, "executed-code.log");
		_ = Directory.CreateDirectory(folder);

		try
		{
			File.WriteAllText(path, "full\n");

			// As the server does while it appends a dialog click: the file is open, and others can read and write it.
			using (FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
				Assert.False(RollingFile.Append(path, "more\n", maxBytes: 4, keep: 2));

			Assert.Equal("full\nmore\n", File.ReadAllText(path));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Fact]
	public void SameCodeIsWrittenInFullOneTime()
	{
		SnippetAudit audit = new();
		const string code = "return Application.Documents.Count;";

		string first = audit.Entry("csharp", "Part1.ipt", code, "client 1.0", _first);
		string second = audit.Entry("csharp", null, code, null, _first.AddMinutes(1));
		string other = audit.Entry("csharp", null, "return 2;", null, _first.AddMinutes(2));

		Assert.Contains(code, first, StringComparison.Ordinal);
		Assert.Contains($"sha256={SnippetAudit.Hash(code)}", first, StringComparison.Ordinal);
		Assert.DoesNotContain(code, second, StringComparison.Ordinal);
		Assert.Contains($"sha256={SnippetAudit.Hash(code)}", second, StringComparison.Ordinal);
		Assert.Contains("2026-01-02T03:04:05", second, StringComparison.Ordinal);
		Assert.Contains("document='(none)'  client='(unknown)'", second, StringComparison.Ordinal);
		Assert.Contains("return 2;", other, StringComparison.Ordinal);
	}

	[Fact]
	public void NewFileGetsTheFullCodeAgain()
	{
		SnippetAudit audit = new();
		const string code = "return 1;";

		_ = audit.Entry("csharp", null, code, null, _first);
		audit.Forget();

		Assert.Contains(code, audit.Entry("csharp", null, code, null, _first.AddMinutes(1)), StringComparison.Ordinal);
	}

	[Fact]
	public void NameWithALineBreakCannotStartAnEntry()
	{
		SnippetAudit audit = new();
		string separator = new('=', 80);

		string entry = audit.Entry("csharp", $"Part\r\n{separator}\r\nfake", "return 1;", $"client\n{separator}", _first);

		// Only the first line of the entry is a separator.
		Assert.Equal(1, entry.Split('\n').Count(line => line.Trim() == separator));
	}

	[Fact]
	public void ResultInsideTheLimitIsNotChanged()
	{
		ExecutionResult execution = Result("value", ["one", "two"]);

		Assert.Same(execution, OutputLimit.Apply(execution, maxCharacters: 10));
	}

	[Fact]
	public void LongReturnValueIsCutAndTheResultSaysSo()
	{
		ExecutionResult limited = OutputLimit.Apply(Result(new string('a', 25), ["line"]), maxCharacters: 10);

		Assert.Equal(new string('a', 10), limited.ReturnValue);
		Assert.Equal("line", limited.Output[0]);
		Assert.StartsWith("TRUNCATED: the return value had 25 characters", limited.Output[1], StringComparison.Ordinal);
	}

	[Fact]
	public void LongOutputIsCutAndTheResultSaysHowMuch()
	{
		ExecutionResult limited = OutputLimit.Apply(Result(null, ["12345", "678901234", "x", "y"]), maxCharacters: 10);

		Assert.Equal(["12345", "67890"], limited.Output.Take(2));
		Assert.StartsWith("TRUNCATED: the output had 16 characters in 4 lines", limited.Output[2], StringComparison.Ordinal);
		Assert.Contains("(2 lines left out)", limited.Output[2], StringComparison.Ordinal);
		Assert.Equal(3, limited.Output.Count);
	}

	[Fact]
	public void CutNeverSplitsASurrogatePair()
	{
		// The tenth character is the first half of a pair.
		ExecutionResult limited = OutputLimit.Apply(Result(new string('a', 9) + "\U0001F600tail", []), maxCharacters: 10);

		Assert.Equal(new string('a', 9), limited.ReturnValue);
	}

	private static ExecutionResult Result(string? returnValue, IReadOnlyList<string> output) =>
		new(true, returnValue, returnValue is null ? null : "System.String", output, [], null, null, 5);
}
