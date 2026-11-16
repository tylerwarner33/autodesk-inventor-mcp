using InventorMcp.Contracts;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The pipe name of each Inventor process, and the names of the older add-ins.
/// </summary>
[Trait("Level", "Unit")]
public sealed class PipeNameTests
{
	private const string Prefix = "InventorMcp.Bridge";

	[Theory]
	[InlineData(2025, 4242, "InventorMcp.Bridge.v3.2025.4242")]
	[InlineData(2026, 1, "InventorMcp.Bridge.v3.2026.1")]
	[InlineData(2027, 99999, "InventorMcp.Bridge.v3.2027.99999")]
	public void EachProcessHasItsOwnPipe(int year, int processId, string expected) =>
		Assert.Equal(expected, BridgeProtocol.PipeNameFor(year, processId));

	[Fact]
	public void TwoProcessesOfOneReleaseHaveTwoNames() =>
		Assert.NotEqual(BridgeProtocol.PipeNameFor(2026, 10), BridgeProtocol.PipeNameFor(2026, 11));

	[Fact]
	public void AnInstanceNameRoundTrips()
	{
		PipeNameKind kind = BridgeProtocol.ParsePipeName(BridgeProtocol.PipeNameFor(2026, 12345), Prefix, out int version, out int year, out int processId);

		Assert.Equal(PipeNameKind.Instance, kind);
		Assert.Equal(BridgeProtocol.Version, version);
		Assert.Equal(2026, year);
		Assert.Equal(12345, processId);
	}

	[Fact]
	public void AProtocolTwoNameIsAReleasePipeWithNoProcess()
	{
		PipeNameKind kind = BridgeProtocol.ParsePipeName("InventorMcp.Bridge.2026", Prefix, out int version, out int year, out int processId);

		Assert.Equal(PipeNameKind.ReleasePipe, kind);
		Assert.Equal(2, version);
		Assert.Equal(2026, year);
		Assert.Equal(0, processId);
	}

	[Fact]
	public void TheProtocolOneNameIsLegacyAndIsNotAnInstanceName()
	{
		Assert.Equal(PipeNameKind.Legacy, BridgeProtocol.ParsePipeName(BridgeProtocol.LegacyPipeName, Prefix, out _, out _, out _));
		Assert.DoesNotContain(InventorReleases.Years, static year => BridgeProtocol.PipeNameFor(year, 1) == BridgeProtocol.LegacyPipeName);
	}

	[Fact]
	public void ANewerVersionIsReadWithItsNumber()
	{
		PipeNameKind kind = BridgeProtocol.ParsePipeName("InventorMcp.Bridge.v4.2026.77", Prefix, out int version, out _, out int processId);

		Assert.Equal(PipeNameKind.Instance, kind);
		Assert.Equal(4, version);
		Assert.Equal(77, processId);
	}

	[Theory]
	[InlineData("SomethingElse.v3.2026.1")]
	[InlineData("InventorMcp.Bridge.v3.2026")]
	[InlineData("InventorMcp.Bridge.v3.2026.12.7")]
	[InlineData("InventorMcp.Bridge.v3.abc.1")]
	[InlineData("InventorMcp.Bridge.v3.2026.xyz")]
	[InlineData("InventorMcp.Bridge.vX.2026.1")]
	[InlineData("InventorMcp.Bridge.v.2026.1")]
	[InlineData("InventorMcp.Bridge.2026x")]
	[InlineData("InventorMcp.Bridge.-1")]
	[InlineData("InventorMcp.BridgeExtra")]
	public void AnythingElseIsNotABridgePipe(string name) =>
		Assert.Equal(PipeNameKind.None, BridgeProtocol.ParsePipeName(name, Prefix, out _, out _, out _));

	[Fact]
	public void ProtocolVersionIsThree() => Assert.Equal(3, BridgeProtocol.Version);
}
