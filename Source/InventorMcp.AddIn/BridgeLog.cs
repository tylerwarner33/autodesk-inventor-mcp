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

	/// <summary>
	/// 	Directory holding the add-in log and the executed code audit trail.
	/// </summary>
	public static string LogDirectory { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"InventorMcp");

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
					Path.Combine(LogDirectory, "addin.log"),
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
