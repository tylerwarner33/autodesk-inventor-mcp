using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;
using InventorMcp.Server.Bridge;

namespace InventorMcp.Server.Tests.Live;

/// <summary>
/// 	Two Inventor releases at one time, each with its own bridge.
/// </summary>
/// <remarks>
/// 	Needs two releases running with the add-in loaded, ex. 2025 and 2027, deployed with protocol version 2.
/// 	The test skips when fewer than two bridges answer.
/// </remarks>
[Trait("Level", "Live")]
public sealed class LiveMultiReleaseTests
{
	private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

	[Fact]
	public async Task EachReleaseAnswersWithItsOwnYearAndProcess()
	{
		LiveInventor.Require();

		IReadOnlyList<int> running = new ReleaseSelection().RunningReleases();

		Assert.SkipWhen(running.Count < 2, "Start two Inventor releases with the add-in to run this test.");

		HashSet<int> processIds = [];

		foreach (int year in running)
		{
			await using BridgeClient client = LiveInventor.CreateClient(year);

			SessionInfo session = await client.InvokeAsync<SessionInfo>(BridgeOperations.Session, null, Cancellation);

			Assert.Equal(year, session.ReleaseYear);
			Assert.Equal(year, client.ReleaseYear);
			Assert.NotNull(client.InventorProcessId);
			Assert.True(processIds.Add(client.InventorProcessId!.Value), "Two releases answered from one process.");
		}
	}

	[Fact]
	public async Task ChosenReleaseThatIsNotRunningDoesNotReachAnotherRelease()
	{
		LiveInventor.Require();

		IReadOnlyList<int> running = new ReleaseSelection().RunningReleases();
		int? missing = InventorReleases.Years.Cast<int?>().FirstOrDefault(year => running.Contains(year!.Value) is false);

		Assert.SkipWhen(missing is null || running.Count == 0, "Needs one release that runs and one that does not.");

		await using BridgeClient client = LiveInventor.CreateClient(missing);

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(
			() => client.InvokeAsync<SessionInfo>(BridgeOperations.Session, null, Cancellation));

		Assert.Equal(BridgeErrorCodes.NotRunning, exception.Code);
		Assert.Contains(missing.ToString()!, exception.Message);
	}
}
