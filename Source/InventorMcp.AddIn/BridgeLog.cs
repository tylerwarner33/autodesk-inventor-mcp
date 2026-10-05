namespace InventorMcp.AddIn;

/// <summary>
/// 	Minimal file logger for the add-in.
/// </summary>
/// <remarks>
/// 	The add-in runs inside Inventor.exe and has no console.
/// 	Logging stays deliberately dependency free so a logging package can never conflict with one Inventor already loaded.
/// </remarks>
internal static class BridgeLog
{
	private static readonly Lock _gate = new();

	private static int? _releaseYear;

	/// <summary>
	/// 	Directory holding the add-in log and the executed code audit trail.
	/// </summary>
	public static string LogDirectory { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"InventorMcp");

	/// <summary>
	/// 	Path of the executed code audit trail of this Inventor release.
	/// </summary>
	public static string AuditLogPath => Path.Combine(LogDirectory, FileName("executed-code"));

	/// <summary>
	/// 	Gives each release its own log files, so two Inventor processes never append to one file.
	/// </summary>
	/// <remarks>
	/// 	The lock in this class covers one process only.
	/// 	Until this runs, lines go to the file with no year, ex. a failure before the release is known.
	/// </remarks>
	/// <param name="releaseYear">
	/// 	The release that runs this add-in, ex. 2026.
	/// </param>
	public static void UseRelease(int releaseYear) => _releaseYear = releaseYear;

	/// <summary>
	/// 	Appends one timestamped line to the add-in log.
	/// </summary>
	/// <param name="message">
	/// 	Text to record.
	/// </param>
	public static void Write(string message)
	{
		try
		{
			lock (_gate)
			{
				_ = Directory.CreateDirectory(LogDirectory);

				File.AppendAllText(
					Path.Combine(LogDirectory, FileName("addin")),
					$"{DateTimeOffset.UtcNow:O}  {message}{Environment.NewLine}");
			}
		}
		catch (IOException)
		{
			// Logging must never take Inventor down.
		}
		catch (UnauthorizedAccessException)
		{
			// Same.
		}
	}

	private static string FileName(string name) => _releaseYear is int year ? $"{name}.{year}.log" : $"{name}.log";
}
