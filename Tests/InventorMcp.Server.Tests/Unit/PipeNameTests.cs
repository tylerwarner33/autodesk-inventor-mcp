using InventorMcp.Contracts;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The pipe name of each release, and the name of a protocol 1 add-in.
/// </summary>
[Trait("Level", "Unit")]
public sealed class PipeNameTests
{
	[Theory]
	[InlineData(2025, "InventorMcp.Bridge.2025")]
	[InlineData(2026, "InventorMcp.Bridge.2026")]
	[InlineData(2027, "InventorMcp.Bridge.2027")]
	public void EachReleaseHasItsOwnPipe(int year, string expected) => Assert.Equal(expected, BridgeProtocol.PipeNameFor(year));

	[Fact]
	public void LegacyNameIsNotAReleaseName() =>
		Assert.DoesNotContain(InventorReleases.Years, static year => BridgeProtocol.PipeNameFor(year) == BridgeProtocol.LegacyPipeName);

	[Fact]
	public void ProtocolVersionIsTwo() => Assert.Equal(2, BridgeProtocol.Version);
}
