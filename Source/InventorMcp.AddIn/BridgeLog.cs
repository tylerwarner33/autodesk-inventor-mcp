using System.Globalization;

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
	/// 	The root of the logs. Each release writes to its own folder below it, ex. <c>2026</c>.
	/// </summary>
	public static string LogDirectory { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"InventorMcp");

	/// <summary>
	/// 	The folder of this release, or <see cref="LogDirectory"/> before the release is known.
	/// </summary>
	public static string ReleaseDirectory => _releaseYear is int year ? Path.Combine(LogDirectory, year.ToString(CultureInfo.InvariantCulture)) : LogDirectory;

	/// <summary>
	/// 	Path of the executed code audit trail of this Inventor release.
	/// </summary>
	public static string AuditLogPath => Path.Combine(ReleaseDirectory, "executed-code.log");

	/// <summary>
	/// 	Gives each release its own log folder, so two Inventor processes never append to one file.
	/// </summary>
	/// <remarks>
	/// 	The lock in this class covers one process only.
	/// 	Until this runs, lines go to <see cref="LogDirectory"/>, ex. a failure before the release is known.
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
				_ = Directory.CreateDirectory(ReleaseDirectory);

				File.AppendAllText(
					Path.Combine(ReleaseDirectory, "addin.log"),
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
}
