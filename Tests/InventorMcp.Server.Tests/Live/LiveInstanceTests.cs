using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;
using InventorMcp.Server.Bridge;

namespace InventorMcp.Server.Tests.Live;

/// <summary>
/// 	One pipe for each Inventor process, in a live Inventor.
/// </summary>
/// <remarks>
/// 	Needs Inventor with the add-in loaded (protocol version 3). The test of two processes of one release skips unless
/// 	two such Inventor processes run.
/// </remarks>
[Trait("Level", "Live")]
public sealed class LiveInstanceTests
{
	private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

	[Fact]
	public async Task TheProcessInTheNameIsTheProcessThatAnswers()
	{
		LiveInventor.Require();

		IReadOnlyList<PipeInstance> instances = new ReleaseSelection().RunningInstances();

		Assert.SkipWhen(instances.Count == 0, "Start Inventor with the add-in to run this test.");

		foreach (PipeInstance instance in instances)
		{
			await using BridgeClient client = LiveInventor.CreateClient(instance.ReleaseYear, instance.ProcessId);

			SessionInfo session = await client.InvokeAsync<SessionInfo>(BridgeOperations.Session, null, Cancellation);

			// Three sources of the process id must agree: the name, the pipe (GetNamedPipeServerProcessId) and the add-in.
			Assert.Equal(BridgeProtocol.Version, session.BridgeVersion);
			Assert.Equal(instance.ProcessId, session.ProcessId);
			Assert.Equal(instance.ProcessId, client.InventorProcessId);
			Assert.Equal(instance.ReleaseYear, session.ReleaseYear);
		}
	}

	[Fact]
	public async Task TwoProcessesOfOneReleaseNeedAChoiceAndEachAnswersForItself()
	{
		LiveInventor.Require();

		IReadOnlyList<PipeInstance> instances = new ReleaseSelection().RunningInstances();
		int? year = instances.GroupBy(static instance => instance.ReleaseYear).FirstOrDefault(static group => group.Count() >= 2)?.Key;

		Assert.SkipWhen(year is null, "Start two Inventor processes of one release with the add-in to run this test.");

		PipeInstance[] ofRelease = [.. instances.Where(instance => instance.ReleaseYear == year)];

		// With no process chosen, a call is not sent: the session cannot know which Inventor is meant.
		await using (BridgeClient undecided = LiveInventor.CreateClient(year))
		{
			InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(
				() => undecided.InvokeAsync<SessionInfo>(BridgeOperations.Session, null, Cancellation));

			Assert.Equal(BridgeErrorCodes.InstanceRequired, exception.Code);
			Assert.All(ofRelease, instance => Assert.Contains(instance.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Message, StringComparison.Ordinal));
		}

		foreach (PipeInstance instance in ofRelease)
		{
			await using BridgeClient client = LiveInventor.CreateClient(year, instance.ProcessId);

			SessionInfo session = await client.InvokeAsync<SessionInfo>(BridgeOperations.Session, null, Cancellation);

			Assert.Equal(instance.ProcessId, session.ProcessId);
			Assert.Equal(instance.ProcessId, client.InventorProcessId);
		}
	}
}
