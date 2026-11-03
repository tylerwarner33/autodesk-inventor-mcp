using InventorMcp.Server.Telemetry;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The side channel from a tool to the call tool filter.
/// </summary>
[Trait("Level", "Unit")]
public sealed class ToolOutcomeTests
{
	[Fact]
	public async Task ParentSeesTheChangeOfAChildFlowAfterTheAwait()
	{
		ToolOutcome outcome = ToolOutcome.Begin();

		await Task.Run(async () =>
		{
			await Task.Yield();
			ToolOutcome.Current!.MainThreadMilliseconds = 42;
			_ = ToolOutcome.Current.Fail(TelemetryTags.OutcomeBridgeError, "inventor-busy");
		}, TestContext.Current.CancellationToken);

		Assert.Same(outcome, ToolOutcome.Current);
		Assert.Equal(TelemetryTags.OutcomeBridgeError, outcome.Outcome);
		Assert.Equal("inventor-busy", outcome.ErrorType);
		Assert.Equal(42, outcome.MainThreadMilliseconds);
	}

	[Fact]
	public async Task BeginInAChildFlowDoesNotReplaceTheParentsOutcome()
	{
		ToolOutcome outcome = ToolOutcome.Begin();

		_ = await Task.Run(static () => ToolOutcome.Begin().Fail(TelemetryTags.OutcomeException, "System.Exception"), TestContext.Current.CancellationToken);

		Assert.Same(outcome, ToolOutcome.Current);
		Assert.Equal(TelemetryTags.OutcomeOk, outcome.Outcome);
		Assert.Null(outcome.ErrorType);
	}
}
