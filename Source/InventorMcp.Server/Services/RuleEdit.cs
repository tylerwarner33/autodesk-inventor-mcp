using System.Text;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	The text edit of <c>inventor_ilogic_rule_set</c>: at an anchor that occurs exactly once, with the rule's line ends.
/// </summary>
/// <remarks>
/// 	Rule text was dumped and compared by hand in the usage research, so the edit refuses anything that is not
/// 	certain, keeps a backup, and returns a diff.
/// </remarks>
internal static class RuleEdit
{
	private const int _diffContextLines = 2;
	private const int _maxDiffLines = 200;

	/// <summary>
	/// 	The folder of the rule backups.
	/// </summary>
	public static string BackupFolder { get; set; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"InventorMcp",
		"rule-backups");

	/// <summary>
	/// 	Applies one edit to a rule text.
	/// </summary>
	/// <param name="ruleText">
	/// 	The current text.
	/// </param>
	/// <param name="text">
	/// 	The new text.
	/// </param>
	/// <param name="anchor">
	/// 	Text that must occur exactly once, or null for <c>replace-all</c>.
	/// </param>
	/// <param name="mode">
	/// 	<c>replace</c>, <c>insert-before</c>, <c>insert-after</c> or <c>replace-all</c>.
	/// </param>
	/// <param name="edited">
	/// 	The text after the edit.
	/// </param>
	/// <param name="problem">
	/// 	Why nothing changed, or null.
	/// </param>
	/// <returns>
	/// 	True when the edit applied.
	/// </returns>
	public static bool TryApply(string ruleText, string text, string? anchor, string mode, out string edited, out string? problem)
	{
		string newLine = ruleText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		string normalizedText = Normalize(text, newLine);
		edited = ruleText;
		problem = null;

		if (mode == "replace-all")
		{
			edited = normalizedText;
			return true;
		}

		if (string.IsNullOrEmpty(anchor))
		{
			problem = $"Mode '{mode}' needs an anchor: text that occurs exactly once in the rule.";
			return false;
		}

		string normalizedAnchor = Normalize(anchor, newLine);
		int first = ruleText.IndexOf(normalizedAnchor, StringComparison.Ordinal);

		if (first < 0)
		{
			problem = "The anchor does not occur in the rule. Read the rule with inventor_ilogic_rule_get, and copy the anchor from it.";
			return false;
		}

		if (ruleText.IndexOf(normalizedAnchor, first + 1, StringComparison.Ordinal) >= 0)
		{
			int count = CountOccurrences(ruleText, normalizedAnchor);
			problem = $"The anchor occurs {count} times in the rule. Make it longer, so that it occurs exactly once.";
			return false;
		}

		edited = mode switch
		{
			"insert-before" => ruleText.Insert(first, normalizedText),
			"insert-after" => ruleText.Insert(first + normalizedAnchor.Length, normalizedText),
			_ => string.Concat(ruleText.AsSpan(0, first), normalizedText, ruleText.AsSpan(first + normalizedAnchor.Length))
		};

		return true;
	}

	/// <summary>
	/// 	How long a backup is kept. Older backups are deleted when a new one is written.
	/// </summary>
	public static TimeSpan BackupRetention { get; } = TimeSpan.FromDays(30);

	/// <summary>
	/// 	Writes the old text of a rule to a new backup file, and deletes the backups older than
	/// 	<see cref="BackupRetention"/>.
	/// </summary>
	/// <returns>
	/// 	The path of the backup.
	/// </returns>
	public static string WriteBackup(string documentPath, string ruleName, string oldText)
	{
		_ = Directory.CreateDirectory(BackupFolder);
		DeleteOldBackups(DateTime.UtcNow - BackupRetention);

		string name = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{Path.GetFileNameWithoutExtension(documentPath)}_{ruleName}.iLogicVb";
		string path = Path.Combine(BackupFolder, string.Concat(name.Select(static character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)));

		File.WriteAllText(path, oldText);

		return path;
	}

	/// <summary>
	/// 	A short line diff: the lines that changed, with a little context.
	/// </summary>
	/// <remarks>
	/// 	The edit changes one place, so the common first and last lines are enough to find it. No general diff is
	/// 	needed.
	/// </remarks>
	public static string Diff(string oldText, string newText)
	{
		string[] oldLines = SplitLines(oldText);
		string[] newLines = SplitLines(newText);

		int prefix = 0;
		while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix])
			prefix++;

		int suffix = 0;
		while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix
			&& oldLines[oldLines.Length - 1 - suffix] == newLines[newLines.Length - 1 - suffix])
		{
			suffix++;
		}

		if (prefix == oldLines.Length && prefix == newLines.Length)
			return "No change.";

		StringBuilder diff = new();
		int start = Math.Max(0, prefix - _diffContextLines);
		_ = diff.Append("@@ line ").Append(start + 1).Append(" @@\n");

		int lines = 0;

		void Add(char marker, string line)
		{
			if (lines++ < _maxDiffLines)
				_ = diff.Append(marker).Append(' ').Append(line).Append('\n');
		}

		for (int index = start; index < prefix; index++)
			Add(' ', oldLines[index]);

		for (int index = prefix; index < oldLines.Length - suffix; index++)
			Add('-', oldLines[index]);

		for (int index = prefix; index < newLines.Length - suffix; index++)
			Add('+', newLines[index]);

		for (int index = newLines.Length - suffix; index < Math.Min(newLines.Length, newLines.Length - suffix + _diffContextLines); index++)
			Add(' ', newLines[index]);

		if (lines > _maxDiffLines)
			_ = diff.Append(CultureInfoInvariant($"... {lines - _maxDiffLines} more lines\n"));

		return diff.ToString();
	}

	/// <summary>
	/// 	Deletes the backups last written before a time.
	/// </summary>
	/// <remarks>
	/// 	A backup can hold rule text that a user does not want kept forever, so the folder must not grow without limit.
	/// 	A file that cannot be deleted stays, and the next backup tries again.
	/// </remarks>
	/// <param name="olderThanUtc">
	/// 	The time before which a backup is deleted.
	/// </param>
	internal static void DeleteOldBackups(DateTime olderThanUtc)
	{
		foreach (FileInfo backup in new DirectoryInfo(BackupFolder).EnumerateFiles("*.iLogicVb"))
		{
			if (backup.LastWriteTimeUtc >= olderThanUtc)
				continue;

			try
			{
				backup.Delete();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	private static string CultureInfoInvariant(FormattableString text) => FormattableString.Invariant(text);

	private static string Normalize(string text, string newLine) =>
		text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newLine, StringComparison.Ordinal);

	private static string[] SplitLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

	private static int CountOccurrences(string text, string value)
	{
		int count = 0;

		for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
			count++;

		return count;
	}
}