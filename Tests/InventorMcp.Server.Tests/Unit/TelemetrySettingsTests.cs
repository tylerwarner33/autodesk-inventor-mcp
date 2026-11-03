using InventorMcp.Server.Telemetry;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The switch that turns telemetry on, read from a set of variables and not from the process.
/// </summary>
[Trait("Level", "Unit")]
public sealed class TelemetrySettingsTests
{
	[Fact]
	public void OffWithNoEndpoint() => Assert.False(Read().Enabled);

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void OffWithAnEmptyEndpoint(string endpoint) =>
		Assert.False(Read((TelemetrySettings.EndpointVariable, endpoint)).Enabled);

	[Fact]
	public void OnWithAnEndpoint()
	{
		TelemetrySettings settings = Read((TelemetrySettings.EndpointVariable, " http://localhost:4317 "));

		Assert.True(settings.Enabled);
		Assert.Equal("http://localhost:4317", settings.Endpoint);
	}

	[Theory]
	[InlineData("true")]
	[InlineData("TRUE")]
	[InlineData(" True ")]
	public void DisabledWinsOverAnEndpoint(string disabled)
	{
		TelemetrySettings settings = Read(
			(TelemetrySettings.EndpointVariable, "http://localhost:4317"),
			(TelemetrySettings.DisabledVariable, disabled));

		Assert.False(settings.Enabled);
		Assert.Null(settings.Endpoint);
	}

	[Fact]
	public void DisabledFalseKeepsItOn() =>
		Assert.True(Read(
			(TelemetrySettings.EndpointVariable, "http://localhost:4317"),
			(TelemetrySettings.DisabledVariable, "false")).Enabled);

	private static TelemetrySettings Read(params (string Name, string Value)[] variables)
	{
		Dictionary<string, string> values = variables.ToDictionary(static variable => variable.Name, static variable => variable.Value);

		return TelemetrySettings.Read(name => values.GetValueOrDefault(name));
	}
}
