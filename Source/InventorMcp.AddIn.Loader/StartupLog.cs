namespace InventorMcp.AddIn.Loader;

/// <summary>
/// 	Records a failure that happens before the add-in, and so its log, exists.
/// </summary>
/// <remarks>
/// 	Inventor swallows an exception thrown out of 'Activate', so the add-in just never appears.
/// 	This writes its own file, so a startup failure is findable when 'addin.log' does not exist at all.
/// </remarks>
internal static class StartupLog
{
	/// <summary>
	/// 	Appends one timestamped entry describing a failure during add-in startup.
	/// </summary>
	/// <param name="stage">
	/// 	What was being attempted, ex. "loading the isolated add-in".
	/// </param>
	/// <param name="exception">
	/// 	The failure to record, written in full because this is the only record of it.
	/// </param>
	public static void WriteFailure(string stage, Exception exception)
	{
		try
		{
			string directory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"InventorMcp");

			_ = Directory.CreateDirectory(directory);

			File.AppendAllText(
				Path.Combine(directory, "addin-startup.log"),
				$"{DateTimeOffset.UtcNow:O}  Add-in startup failed while {stage}.{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
		}
		catch
		{
			// Logging must never be the reason startup fails harder than it already has.
		}
	}
}
