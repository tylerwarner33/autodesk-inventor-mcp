namespace InventorMcp.AddIn;

/// <summary>
/// 	Appends text to a log file, and starts a new file when the file is full.
/// </summary>
/// <remarks>
/// 	The logs of the add-in had no limit, and each tool call adds to them.
/// 	A full file <c>name.log</c> becomes <c>name.1.log</c>, and each older file moves up one number.
/// 	The server appends to the same audit file from its own process, so a failure to move a file is not an error:
/// 	the text is appended to the full file, and a later call tries again.
/// </remarks>
internal static class RollingFile
{
	/// <summary>
	/// 	The size at which a file is full.
	/// </summary>
	public const long DefaultMaxBytes = 10 * 1024 * 1024;

	/// <summary>
	/// 	How many full files stay beside the current file.
	/// </summary>
	public const int DefaultKeep = 5;

	/// <summary>
	/// 	Appends text to a file.
	/// </summary>
	/// <param name="path">
	/// 	The current file.
	/// </param>
	/// <param name="text">
	/// 	The text to append.
	/// </param>
	/// <param name="maxBytes">
	/// 	The size at which the file is full.
	/// </param>
	/// <param name="keep">
	/// 	How many full files to keep.
	/// </param>
	/// <returns>
	/// 	True when the text is the start of a new file.
	/// </returns>
	/// <exception cref="IOException">
	/// 	The text could not be appended.
	/// </exception>
	public static bool Append(string path, string text, long maxBytes = DefaultMaxBytes, int keep = DefaultKeep)
	{
		FileInfo file = new(path);
		bool rolled = file.Exists && file.Length >= maxBytes && TryRoll(path, keep);

		File.AppendAllText(path, text);

		return rolled;
	}

	private static bool TryRoll(string path, int keep)
	{
		try
		{
			File.Delete(Numbered(path, keep));

			for (int number = keep - 1; number >= 1; number--)
			{
				if (File.Exists(Numbered(path, number)))
					File.Move(Numbered(path, number), Numbered(path, number + 1), overwrite: true);
			}

			File.Move(path, Numbered(path, 1), overwrite: true);

			return true;
		}
		catch (IOException)
		{
			// Another process holds a file. The text still goes to the full file.
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			// Same.
			return false;
		}
	}

	private static string Numbered(string path, int number) =>
		Path.Combine(
			Path.GetDirectoryName(path) ?? string.Empty,
			$"{Path.GetFileNameWithoutExtension(path)}.{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}{Path.GetExtension(path)}");
}
