using System.Reflection;

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
	/// 	The release this loader is built for, from the <c>AutodeskVersion</c> metadata of its assembly.
	/// </summary>
	/// <remarks>
	/// 	The loader runs before the add-in has Inventor, so it cannot ask the application for the release.
	/// 	It is built for 2025 and 2026 only, so its build input is the release.
	/// </remarks>
	private static readonly string _fileName = ReadFileName();

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
				Path.Combine(directory, _fileName),
				$"{DateTimeOffset.UtcNow:O}  Add-in startup failed while {stage}.{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
		}
		catch
		{
			// Logging must never be the reason startup fails harder than it already has.
		}
	}

	private static string ReadFileName()
	{
		string? year = typeof(StartupLog).Assembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(static attribute => attribute.Key == "AutodeskVersion")?.Value;

		return string.IsNullOrEmpty(year) ? "addin-startup.log" : $"addin-startup.{year}.log";
	}
}
