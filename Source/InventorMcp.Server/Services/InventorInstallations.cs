using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	An installed Inventor release the add-in supports.
/// </summary>
/// <param name="Year">
/// 	Release year, ex. 2025.
/// </param>
/// <param name="SoftwareVersion">
/// 	Autodesk's internal version, ex. 29 for 2025.
/// </param>
/// <param name="ExecutablePath">
/// 	Full path of <c>Inventor.exe</c>.
/// </param>
/// <param name="AddInDeployed">
/// 	True when a manifest for this release exists, so Inventor loads the bridge add-in when it starts.
/// </param>
internal sealed record InventorRelease(int Year, int SoftwareVersion, string ExecutablePath, bool AddInDeployed)
{
	/// <summary>
	/// 	The name the user sees, ex. "Autodesk Inventor 2025".
	/// </summary>
	/// <remarks>
	/// 	Built from the year, because the registry's product name changes with the edition.
	/// </remarks>
	public string DisplayName => $"Autodesk Inventor {Year}";
}

/// <summary>
/// 	An <c>Inventor.exe</c> process in the user's Windows session.
/// </summary>
/// <param name="ProcessId">
/// 	Process identifier.
/// </param>
/// <param name="ExecutablePath">
/// 	Full path of the executable, or null when Windows refuses to tell.
/// </param>
/// <param name="StartTime">
/// 	Local start time, or null when Windows refuses to tell.
/// </param>
/// <param name="HasMainWindow">
/// 	False for a hidden instance, ex. one started through COM with <c>/Embedding</c>.
/// </param>
internal sealed record RunningInventor(int ProcessId, string? ExecutablePath, DateTime? StartTime, bool HasMainWindow);

/// <summary>
/// 	Finds the Inventor releases installed on this machine, and the Inventor processes running now.
/// </summary>
internal sealed class InventorInstallations(ILogger<InventorInstallations> logger)
{
	/// <summary>
	/// 	Limits the releases reported, ex. "2025" or "2025,2027".
	/// </summary>
	/// <remarks>
	/// 	Exists to test the one release path on a machine with several.
	/// </remarks>
	public const string ReleaseFilterVariable = "INVENTOR_MCP_RELEASES";

	private const string _addInName = "InventorMcp.AddIn";

	/// <summary>
	/// 	Release year to software version, for every release the add-in supports.
	/// </summary>
	/// <remarks>
	/// 	Keep in step with <c>SupportedAutodeskVersions</c> and <c>InventorSoftwareVersion</c> in <c>Directory.Build.props</c>.
	/// </remarks>
	private static readonly IReadOnlyDictionary<int, int> _softwareVersionByYear = new Dictionary<int, int>
	{
		[2025] = 29,
		[2026] = 30,
		[2027] = 31
	};

	private readonly ILogger<InventorInstallations> _logger = logger;

	/// <summary>
	/// 	Lists the supported releases installed on this machine, oldest first.
	/// </summary>
	/// <returns>
	/// 	The installed releases, with whether the add-in is deployed for each.
	/// </returns>
	public IReadOnlyList<InventorRelease> FindInstalled()
	{
		HashSet<int>? filter = ReadReleaseFilter();

		// Inventor is 64-bit only, so its keys live in the 64-bit view whatever the server's own bitness.
		using RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

		List<InventorRelease> releases = [];

		foreach ((int year, int softwareVersion) in _softwareVersionByYear.OrderBy(static pair => pair.Key))
		{
			if (filter is not null && filter.Contains(year) is false)
				continue;

			using RegistryKey? key = localMachine.OpenSubKey($@"SOFTWARE\Autodesk\Inventor\RegistryVersion{softwareVersion}.0");

			if (key?.GetValue("InstallLocation") is not string installLocation || string.IsNullOrWhiteSpace(installLocation))
				continue;

			// Inventor LT does not load add-ins, so it can never host the bridge.
			if (key.GetValue("ProductName") is string productName && productName.Contains(" LT ", StringComparison.Ordinal))
			{
				_logger.LogInformation("Ignoring {ProductName}, which does not load add-ins.", productName);
				continue;
			}

			string executablePath = Path.Combine(installLocation, "Bin", "Inventor.exe");

			if (File.Exists(executablePath) is false)
			{
				_logger.LogWarning("Inventor {Year} is registered at {InstallLocation}, but {ExecutablePath} does not exist.", year, installLocation, executablePath);
				continue;
			}

			releases.Add(new InventorRelease(year, softwareVersion, executablePath, IsAddInDeployed(year)));
		}

		return releases;
	}

	/// <summary>
	/// 	Lists the <c>Inventor.exe</c> processes in the user's Windows session.
	/// </summary>
	/// <remarks>
	/// 	Other users' sessions are left out, because their Inventor cannot be the one this user means.
	/// </remarks>
	/// <returns>
	/// 	The running processes, or an empty list.
	/// </returns>
	public static IReadOnlyList<RunningInventor> FindRunning()
	{
		int sessionId = Process.GetCurrentProcess().SessionId;

		List<RunningInventor> running = [];

		foreach (Process process in Process.GetProcessesByName("Inventor"))
		{
			using (process)
			{
				if (process.SessionId != sessionId)
					continue;

				running.Add(new RunningInventor(
					process.Id,
					TryRead(() => process.MainModule?.FileName),
					TryRead<DateTime?>(() => process.StartTime),
					TryRead(() => process.MainWindowHandle != IntPtr.Zero)));
			}
		}

		return running;
	}

	/// <summary>
	/// 	True when a bundle or development manifest exists for the release.
	/// </summary>
	/// <remarks>
	/// 	The paths match <c>InventorBundleDir</c> and <c>InventorAddinsDir</c> in <c>InventorMcp.AddIn.csproj</c>.
	/// </remarks>
	private static bool IsAddInDeployed(int year)
	{
		string applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

		string bundleManifest = Path.Combine(applicationData, "Autodesk", "ApplicationPlugins", _addInName, $"{_addInName}.{year}.addin");
		string developmentManifest = Path.Combine(applicationData, "Autodesk", $"Inventor {year}", "Addins", $"{_addInName}.addin");

		return File.Exists(bundleManifest) || File.Exists(developmentManifest);
	}

	private static HashSet<int>? ReadReleaseFilter()
	{
		string? value = Environment.GetEnvironmentVariable(ReleaseFilterVariable);

		if (string.IsNullOrWhiteSpace(value))
			return null;

		return [.. value
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(static year => int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0)
			.Where(static year => year != 0)];
	}

	/// <summary>
	/// 	Reads a process property Windows can refuse, ex. the module of an elevated process.
	/// </summary>
	private static T? TryRead<T>(Func<T> read)
	{
		try
		{
			return read();
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
		{
			return default;
		}
	}
}
