using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests.Unit;

[Trait("Level", "Unit")]
public sealed class RuleEditTests
{
	private const string _rule = "Sub Main()\r\n\tWidth = 10\r\n\tHeight = 20\r\nEnd Sub";

	[Fact]
	public void ReplaceAtAUniqueAnchorKeepsTheRuleLineEnds()
	{
		Assert.True(RuleEdit.TryApply(_rule, "\tWidth = 12\n\tDepth = 5", "\tWidth = 10", "replace", out string edited, out _));

		Assert.Equal("Sub Main()\r\n\tWidth = 12\r\n\tDepth = 5\r\n\tHeight = 20\r\nEnd Sub", edited);
	}

	[Fact]
	public void InsertAfterAndBefore()
	{
		Assert.True(RuleEdit.TryApply(_rule, "\r\n\tDepth = 5", "\tHeight = 20", "insert-after", out string after, out _));
		Assert.True(RuleEdit.TryApply(_rule, "' Sizes\r\n", "\tWidth = 10", "insert-before", out string before, out _));

		Assert.Contains("\tHeight = 20\r\n\tDepth = 5\r\nEnd Sub", after);
		Assert.Contains("Sub Main()\r\n' Sizes\r\n\tWidth = 10", before);
	}

	[Fact]
	public void AnchorThatOccursTwiceChangesNothing()
	{
		Assert.False(RuleEdit.TryApply(_rule, "x", " = ", "replace", out string edited, out string? problem));

		Assert.Equal(_rule, edited);
		Assert.Contains("occurs 2 times", problem);
	}

	[Fact]
	public void MissingAnchorChangesNothing()
	{
		Assert.False(RuleEdit.TryApply(_rule, "x", "Depth", "replace", out _, out string? problem));

		Assert.Contains("does not occur", problem);
	}

	[Fact]
	public void AnchorWithOtherLineEndsStillMatches()
	{
		Assert.True(RuleEdit.TryApply(_rule, "Sub Main()\n", "Sub Main()\n", "replace", out _, out _));
	}

	[Fact]
	public void DiffShowsOnlyTheChangeWithContext()
	{
		string diff = RuleEdit.Diff("a\nb\nc\nd\ne\nf", "a\nb\nc\nX\ne\nf");

		Assert.Equal("@@ line 2 @@\n  b\n  c\n- d\n+ X\n  e\n  f\n", diff);
	}

	[Fact]
	public void BackupHoldsTheOldText()
	{
		string folder = Path.Combine(Path.GetTempPath(), "InventorMcp.Tests", Guid.NewGuid().ToString("N"));
		string previous = RuleEdit.BackupFolder;
		RuleEdit.BackupFolder = folder;

		try
		{
			string path = RuleEdit.WriteBackup(@"C:\Designs\Top Assembly.iam", "Main Rule", _rule);

			Assert.Equal(_rule, File.ReadAllText(path));
			Assert.EndsWith("_Top Assembly_Main Rule.iLogicVb", path);
		}
		finally
		{
			RuleEdit.BackupFolder = previous;
			Directory.Delete(folder, recursive: true);
		}
	}

	[Fact]
	public void BackupDeletesOnlyTheExpiredBackups()
	{
		string folder = Path.Combine(Path.GetTempPath(), "InventorMcp.Tests", Guid.NewGuid().ToString("N"));
		string previous = RuleEdit.BackupFolder;
		RuleEdit.BackupFolder = folder;

		try
		{
			string expired = RuleEdit.WriteBackup(@"C:\Work\Old.iam", "Main Rule", _rule);
			string recent = RuleEdit.WriteBackup(@"C:\Work\Recent.iam", "Main Rule", _rule);
			string other = Path.Combine(folder, "notes.txt");
			File.WriteAllText(other, "kept");
			File.SetLastWriteTimeUtc(expired, DateTime.UtcNow - RuleEdit.BackupRetention - TimeSpan.FromDays(1));
			File.SetLastWriteTimeUtc(other, DateTime.UtcNow - RuleEdit.BackupRetention - TimeSpan.FromDays(1));

			string newest = RuleEdit.WriteBackup(@"C:\Work\New.iam", "Main Rule", _rule);

			Assert.False(File.Exists(expired));
			Assert.True(File.Exists(recent));
			Assert.True(File.Exists(newest));
			Assert.True(File.Exists(other));
		}
		finally
		{
			RuleEdit.BackupFolder = previous;
			Directory.Delete(folder, recursive: true);
		}
	}
}