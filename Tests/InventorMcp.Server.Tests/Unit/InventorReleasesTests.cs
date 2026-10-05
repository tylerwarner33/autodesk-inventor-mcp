using InventorMcp.Contracts;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The release map that the server and the add-in share.
/// </summary>
[Trait("Level", "Unit")]
public sealed class InventorReleasesTests
{
	[Theory]
	[InlineData(29, 2025)]
	[InlineData(30, 2026)]
	[InlineData(31, 2027)]
	public void SoftwareVersionMapsToTheYear(int softwareVersion, int expectedYear)
	{
		Assert.True(InventorReleases.TryGetYear(softwareVersion, out int year));
		Assert.Equal(expectedYear, year);
	}

	[Theory]
	[InlineData(2025, 29)]
	[InlineData(2026, 30)]
	[InlineData(2027, 31)]
	public void YearMapsToTheSoftwareVersion(int year, int expectedSoftwareVersion)
	{
		Assert.True(InventorReleases.TryGetSoftwareVersion(year, out int softwareVersion));
		Assert.Equal(expectedSoftwareVersion, softwareVersion);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(28)]
	[InlineData(32)]
	public void UnknownSoftwareVersionIsRefused(int softwareVersion)
	{
		Assert.False(InventorReleases.TryGetYear(softwareVersion, out int year));
		Assert.Equal(0, year);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(2024)]
	[InlineData(2028)]
	public void UnknownYearIsRefused(int year)
	{
		Assert.False(InventorReleases.TryGetSoftwareVersion(year, out int softwareVersion));
		Assert.Equal(0, softwareVersion);
	}

	[Fact]
	public void YearsAreOldestFirst() => Assert.Equal([2025, 2026, 2027], InventorReleases.Years);
}
