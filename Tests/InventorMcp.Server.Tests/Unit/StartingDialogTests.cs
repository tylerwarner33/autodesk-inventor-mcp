using System.Diagnostics;

using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	A dialog that holds Inventor before the add-in loads is found through the process list, with no pipe.
/// </summary>
[Trait("Level", "Unit")]
public sealed class StartingDialogTests
{
	private static readonly DialogSnapshot _logIn = new(0x1234, "Log In", "WindowsForms10.Window.8.app.0.ffc8c_r3_ad1", "WinForm", "Vault:", []);

	[Fact]
	public async Task OneStartingInventorIsExamined()
	{
		await using TestBridge bridge = new(running: [Running(4242)]);
		bridge.Dialogs.Dialogs = [_logIn];

		BlockState? state = await bridge.Client.GetStartingBlockStateAsync(TestContext.Current.CancellationToken);

		Assert.NotNull(state);
		Assert.True(state.Blocked);
		Assert.Equal(4242, state.ProcessId);
		Assert.Equal("Log In", state.Summary);
	}

	/// <summary>
	/// 	A client pipe stays connected after its Inventor closed. A new start must not take it as the new bridge.
	/// </summary>
	[Fact]
	public async Task PipeOfAClosedInventorIsNotUsed()
	{
		bool hostRuns = true;
		await using TestBridge bridge = new(running: [Running(4242)], processRuns: _ => hostRuns);
		bridge.Dialogs.Dialogs = [_logIn];

		Task<TestConnection> accept = bridge.Server.AcceptAsync();
		Assert.True(await bridge.Client.TryConnectAsync(TestContext.Current.CancellationToken));
		_ = await accept;
		Assert.Null(await bridge.Client.GetStartingBlockStateAsync(TestContext.Current.CancellationToken));

		hostRuns = false;

		Assert.False(bridge.Client.IsConnected);
		// The new Inventor is examined through the process list, not the closed one through the old pipe.
		BlockState? state = await bridge.Client.GetStartingBlockStateAsync(TestContext.Current.CancellationToken);
		Assert.Equal(4242, state?.ProcessId);
		Assert.False(await bridge.Client.TryConnectAsync(TestContext.Current.CancellationToken));
		Assert.Null(bridge.Client.InventorProcessId);
	}

	[Fact]
	public async Task ProcessThatEndedDoesNotRun()
	{
		using Process process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);

		Assert.True(BridgeClient.ProcessRuns(Environment.ProcessId));
		Assert.False(BridgeClient.ProcessRuns(process.Id));
	}

	[Fact]
	public async Task NoStartingInventorGivesNoState()
	{
		await using TestBridge bridge = new();
		bridge.Dialogs.Dialogs = [_logIn];

		Assert.Null(await bridge.Client.GetStartingBlockStateAsync(TestContext.Current.CancellationToken));
		Assert.Equal(0, bridge.Dialogs.DetectCalls);
	}

	[Fact]
	public async Task TwoStartingInventorsAreNotGuessed()
	{
		await using TestBridge bridge = new(running: [Running(4242), Running(4343)]);
		bridge.Dialogs.Dialogs = [_logIn];

		Assert.Null(await bridge.Client.GetStartingBlockStateAsync(TestContext.Current.CancellationToken));
		Assert.Equal(0, bridge.Dialogs.DetectCalls);
	}

	private static RunningInventor Running(int processId) => new(processId, null, null, HasMainWindow: true, ReleaseYear: 2025);
}
