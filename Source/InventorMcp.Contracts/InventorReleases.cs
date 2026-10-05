namespace InventorMcp.Contracts;

/// <summary>
/// 	The Inventor releases the add-in supports, and the software version each one reports.
/// </summary>
/// <remarks>
/// 	The one copy that the server and the add-in share.
/// 	Keep in step with <c>SupportedAutodeskVersions</c> and <c>InventorSoftwareVersion</c> in <c>Directory.Build.props</c>.
/// </remarks>
public static class InventorReleases
{
	private static readonly IReadOnlyDictionary<int, int> _softwareVersionByYear = new Dictionary<int, int>
	{
		[2025] = 29,
		[2026] = 30,
		[2027] = 31
	};

	/// <summary>
	/// 	Every supported release year, oldest first.
	/// </summary>
	public static IReadOnlyList<int> Years { get; } = [.. _softwareVersionByYear.Keys.Order()];

	/// <summary>
	/// 	Maps Autodesk's software version to the release year.
	/// </summary>
	/// <param name="softwareVersion">
	/// 	The major part of the version, ex. 29 for 2025.
	/// </param>
	/// <param name="year">
	/// 	The release year, or 0 when the version is not supported.
	/// </param>
	/// <returns>
	/// 	True when the version is supported.
	/// </returns>
	public static bool TryGetYear(int softwareVersion, out int year)
	{
		foreach ((int candidate, int version) in _softwareVersionByYear)
		{
			if (version == softwareVersion)
			{
				year = candidate;
				return true;
			}
		}

		year = 0;
		return false;
	}

	/// <summary>
	/// 	Maps the release year to Autodesk's software version.
	/// </summary>
	/// <param name="year">
	/// 	The release year, ex. 2025.
	/// </param>
	/// <param name="softwareVersion">
	/// 	The major part of the version, or 0 when the release is not supported.
	/// </param>
	/// <returns>
	/// 	True when the release is supported.
	/// </returns>
	public static bool TryGetSoftwareVersion(int year, out int softwareVersion) =>
		_softwareVersionByYear.TryGetValue(year, out softwareVersion);
}
