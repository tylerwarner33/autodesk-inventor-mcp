using System.Diagnostics;

using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;
using InventorMcp.Server.Tests.Desktop;

namespace InventorMcp.Server.Tests.Live;

/// <summary>
/// 	One pipe for each Inventor process, in a live Inventor.
/// </summary>
/// <remarks>
/// 	Needs Inventor with the add-in loaded (protocol version 3). The tests of two processes of one release skip unless
/// 	two such Inventor processes run. One test shows a dialog, so the class is in the collection of the dialog tests.
/// </remarks>
[Trait("Level", "Live")]
[Collection(DesktopCollection.Name)]
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
			Assert.Equal(instance.ProcessId, session.ProcessId);
			Assert.Equal(instance.ProcessId, client.InventorProcessId);
			Assert.Equal(instance.ReleaseYear, session.ReleaseYear);
		}
	}

	[Fact]
	public async Task TwoProcessesOfOneReleaseNeedAChoiceAndEachAnswersForItself()
	{
		LiveInventor.Require();

		PipeInstance[] ofRelease = TwoProcessesOfOneRelease();
		int year = ofRelease[0].ReleaseYear;

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

	[Fact]
	public async Task ADialogInTheOtherProcessDoesNotBlockThisSession()
	{
		LiveInventor.Require();

		PipeInstance[] ofRelease = TwoProcessesOfOneRelease();
		PipeInstance chosen = ofRelease[0];
		PipeInstance other = ofRelease[1];

		await using BridgeClient blocked = LiveInventor.CreateClient(other.ReleaseYear, other.ProcessId);
		await using BridgeClient client = LiveInventor.CreateClient(chosen.ReleaseYear, chosen.ProcessId);

		Task<ExecutionResult> question = LiveInventor.EvalAsync(blocked, LiveInventor.MessageBoxSnippet("McpTest Other Process", "YesNo"), Cancellation);
		(int processId, DialogSnapshot dialog) = await LiveInventor.WaitForDialogAsync(blocked, Cancellation);

		try
		{
			Assert.Equal(other.ProcessId, processId);

			Stopwatch stopwatch = Stopwatch.StartNew();
			BlockState? state = await client.GetBlockStateAsync(Cancellation);
			ExecutionResult result = await LiveInventor.EvalAsync(client, "return \"not blocked\";", Cancellation);
			stopwatch.Stop();

			Assert.NotEqual(true, state?.Blocked);
			Assert.Equal("not blocked", result.ReturnValue);
			Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"The chosen process took {stopwatch.Elapsed}.");
		}
		finally
		{
			// The question is for a person, so the test answers it only to close it.
			_ = new BlockingDialogs().TryClick(processId, dialog, "No");
			_ = await question.ContinueWith(static _ => 0, Cancellation).WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
		}
	}

	/// <summary>
	/// 	Two running processes of the first release that has two, or skips the test.
	/// </summary>
	private static PipeInstance[] TwoProcessesOfOneRelease()
	{
		IReadOnlyList<PipeInstance> instances = new ReleaseSelection().RunningInstances();
		int? year = instances.GroupBy(static instance => instance.ReleaseYear).FirstOrDefault(static group => group.Count() >= 2)?.Key;

		Assert.SkipWhen(year is null, "Start two Inventor processes of one release with the add-in to run this test.");

		return [.. instances.Where(instance => instance.ReleaseYear == year)];
	}
}
