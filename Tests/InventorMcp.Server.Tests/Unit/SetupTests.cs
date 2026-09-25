namespace InventorMcp.Server.Tests.Unit;

[Trait("Level", "Unit")]
public sealed class SetupTests
{
	[Fact]
	public void FixtureExeIsInTheTestOutput()
	{
		string fixture = Path.Combine(AppContext.BaseDirectory, "InventorMcp.TestDialogs.exe");

		Assert.True(File.Exists(fixture), $"Not found: {fixture}");
		Assert.True(File.Exists(Path.ChangeExtension(fixture, ".runtimeconfig.json")), "The fixture runtimeconfig.json is missing.");
	}
}