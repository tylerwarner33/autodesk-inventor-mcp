using System.IO.Pipes;

using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using Microsoft.Extensions.Logging;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The cause of a connect that fails, and the message that tells the model what to do next.
/// </summary>
[Trait("Level", "Unit")]
public sealed class ConnectionDiagnosisTests
{
	private static readonly DateTime _now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Local);

	[Fact]
	public void APipeThatExistsMeansEveryConnectionIsInUse()
	{
		ConnectionCause cause = ConnectionDiagnosis.Classify(true, [Inventor(_now.AddSeconds(-10))], true, _now);

		Assert.Equal(ConnectionCause.ConnectionsInUse, cause);
	}

	[Fact]
	public void NoPipeAndNoProcessMeansInventorDoesNotRun() =>
		Assert.Equal(ConnectionCause.NotRunning, ConnectionDiagnosis.Classify(false, [], true, _now));

	[Fact]
	public void ARunningInventorWithNoManifestMeansTheAddInIsNotDeployed() =>
		Assert.Equal(ConnectionCause.AddInNotDeployed, ConnectionDiagnosis.Classify(false, [Inventor(_now.AddSeconds(-10))], false, _now));

	[Fact]
	public void ANewProcessMeansTheAddInIsLoading() =>
		Assert.Equal(ConnectionCause.AddInLoading, ConnectionDiagnosis.Classify(false, [Inventor(_now.AddSeconds(-30))], true, _now));

	[Fact]
	public void TheNewestProcessDecidesWhetherTheAddInIsLoading() =>
		Assert.Equal(
			ConnectionCause.AddInLoading,
			ConnectionDiagnosis.Classify(false, [Inventor(_now.AddHours(-5)), Inventor(_now.AddSeconds(-30))], true, _now));

	[Fact]
	public void AnOldProcessMeansTheAddInIsNotLoaded() =>
		Assert.Equal(ConnectionCause.AddInNotLoaded, ConnectionDiagnosis.Classify(false, [Inventor(_now.AddMinutes(-10))], true, _now));

	[Fact]
	public void TheLoadingWindowEndsAtOneHundredTwentySeconds()
	{
		Assert.Equal(ConnectionCause.AddInLoading, ConnectionDiagnosis.Classify(false, [Inventor(_now.AddSeconds(-119))], true, _now));
		Assert.Equal(ConnectionCause.AddInNotLoaded, ConnectionDiagnosis.Classify(false, [Inventor(_now.AddSeconds(-120))], true, _now));
	}

	[Fact]
	public void AProcessWithNoStartTimeCountsAsOld() =>
		Assert.Equal(ConnectionCause.AddInNotLoaded, ConnectionDiagnosis.Classify(false, [Inventor(null)], true, _now));

	[Fact]
	public void OnlyAFullPipeHasItsOwnCode()
	{
		IReadOnlyList<RunningInventor> running = [Inventor(_now)];

		Assert.Equal(BridgeErrorCodes.ConnectionsInUse, ConnectionDiagnosis.Describe(ConnectionCause.ConnectionsInUse, 2025, running).Code);

		foreach (ConnectionCause cause in new[] { ConnectionCause.NotRunning, ConnectionCause.AddInNotDeployed, ConnectionCause.AddInLoading, ConnectionCause.AddInNotLoaded })
			Assert.Equal(BridgeErrorCodes.NotRunning, ConnectionDiagnosis.Describe(cause, 2025, running).Code);
	}

	[Fact]
	public void EachCauseTellsTheModelTheNextStep()
	{
		IReadOnlyList<RunningInventor> running = [Inventor(_now)];

		string inUse = ConnectionDiagnosis.Describe(ConnectionCause.ConnectionsInUse, 2025, running).Message;
		string notRunning = ConnectionDiagnosis.Describe(ConnectionCause.NotRunning, 2025, []).Message;
		string notDeployed = ConnectionDiagnosis.Describe(ConnectionCause.AddInNotDeployed, 2025, running).Message;
		string loading = ConnectionDiagnosis.Describe(ConnectionCause.AddInLoading, 2025, running).Message;
		string notLoaded = ConnectionDiagnosis.Describe(ConnectionCause.AddInNotLoaded, 2025, running).Message;

		Assert.Contains("close an MCP client", inUse, StringComparison.Ordinal);
		Assert.Contains("Do not call inventor_start", inUse, StringComparison.Ordinal);
		Assert.Contains("Do not wait", inUse, StringComparison.Ordinal);
		Assert.Contains("then call inventor_start with version 2025", notRunning, StringComparison.Ordinal);
		Assert.Contains("not deployed", notDeployed, StringComparison.Ordinal);
		Assert.Contains("Do not call inventor_start", notDeployed, StringComparison.Ordinal);
		Assert.Contains("waitSeconds", loading, StringComparison.Ordinal);
		Assert.Contains("Do not call inventor_start", loading, StringComparison.Ordinal);
		Assert.Contains("Tools > Add-Ins", notLoaded, StringComparison.Ordinal);
		Assert.Contains("addin-startup.log", notLoaded, StringComparison.Ordinal);
		Assert.Contains("Do not call inventor_start", notLoaded, StringComparison.Ordinal);
	}

	[Fact]
	public void AHiddenInstanceIsNamedInTheLastTwoCauses()
	{
		IReadOnlyList<RunningInventor> hidden = [Inventor(_now) with { HasMainWindow = false }];

		Assert.Contains("hidden instance", ConnectionDiagnosis.Describe(ConnectionCause.AddInLoading, 2025, hidden).Message, StringComparison.Ordinal);
		Assert.Contains("hidden instance", ConnectionDiagnosis.Describe(ConnectionCause.AddInNotLoaded, 2025, hidden).Message, StringComparison.Ordinal);
		Assert.DoesNotContain("hidden instance", ConnectionDiagnosis.Describe(ConnectionCause.AddInNotLoaded, 2025, [Inventor(_now)]).Message, StringComparison.Ordinal);
	}

	[Fact]
	public void WithNoReleaseKnownTheMessageStaysGeneral()
	{
		string message = ConnectionDiagnosis.Describe(ConnectionCause.NotRunning, null, []).Message;

		Assert.StartsWith("No Inventor session is hosting the MCP bridge.", message, StringComparison.Ordinal);
	}

	[Fact]
	public void ADropNamesWhetherTheProcessStillRuns()
	{
		Assert.Contains("still runs", ConnectionDiagnosis.DescribeDrop(true), StringComparison.Ordinal);
		Assert.Contains("no longer runs", ConnectionDiagnosis.DescribeDrop(false), StringComparison.Ordinal);
		Assert.Contains("not known", ConnectionDiagnosis.DescribeDrop(null), StringComparison.Ordinal);
	}

	[Fact]
	public void AReleaseSelectionTellsIfThePipeExistsFromTheListAlone()
	{
		ReleaseSelection selection = new("Prefix", environmentValue: string.Empty, listPipes: static () => ["prefix.v3.2025.100"]);

		Assert.True(selection.PipeExists("prefix.v3.2025.100"));
		Assert.False(selection.PipeExists("prefix.v3.2026.100"));
	}

	[Fact]
	public async Task APipeWithEveryInstanceInUseGivesItsOwnCode()
	{
		await using TestBridge bridge = new(findRunning: static _ => [Inventor(DateTime.Now.AddHours(-1))]);

		// One instance, and it is connected, so the pipe exists and no client can connect.
		await using NamedPipeServerStream occupied = new(bridge.Server.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
		Task connected = occupied.WaitForConnectionAsync(TestContext.Current.CancellationToken);
		await using NamedPipeClientStream other = new(".", bridge.Server.PipeName, PipeDirection.InOut);
		await other.ConnectAsync(5_000, TestContext.Current.CancellationToken);
		await connected;

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(
			() => bridge.Client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken));

		Assert.Equal(BridgeErrorCodes.ConnectionsInUse, exception.Code);
		Assert.Contains("Do not call inventor_start", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ANewInventorWithNoPipeKeepsTheNotRunningCodeAndSaysLoading()
	{
		TestBridge? holder = null;
		holder = new TestBridge(findRunning: _ => [Inventor(holder!.Time.GetLocalNow().DateTime.AddSeconds(-10))]);

		await using TestBridge bridge = holder;

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(
			() => bridge.Client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken));

		Assert.Equal(BridgeErrorCodes.NotRunning, exception.Code);
		Assert.Contains("still loading", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnOldInventorWithNoPipeSaysThatTheAddInIsNotLoaded()
	{
		TestBridge? holder = null;
		holder = new TestBridge(findRunning: _ => [Inventor(holder!.Time.GetLocalNow().DateTime.AddMinutes(-10))]);

		await using TestBridge bridge = holder;

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(
			() => bridge.Client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken));

		Assert.Equal(BridgeErrorCodes.NotRunning, exception.Code);
		Assert.Contains("is not loaded", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnInventorWithNoManifestSaysThatTheAddInIsNotDeployed()
	{
		await using TestBridge bridge = new(running: [Inventor(null)], isAddInDeployed: static _ => false);

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(
			() => bridge.Client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken));

		Assert.Equal(BridgeErrorCodes.NotRunning, exception.Code);
		Assert.Contains("not deployed", exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(true, "still runs")]
	[InlineData(false, "no longer runs")]
	public async Task ADroppedPipeLogsWhetherTheProcessStillRuns(bool processRuns, string expected)
	{
		RecordingLogger logger = new();
		await using TestBridge bridge = new(processRuns: _ => processRuns, logger: logger);
		(Task<string> call, TestConnection first, _) = await bridge.StartCallAsync();

		Task<TestConnection> accept = bridge.Server.AcceptAsync();
		first.Disconnect();

		TestConnection second = await accept;
		TestRequest retry = await second.ReadRequestAsync();
		await second.RespondAsync(retry.Id, "fresh");

		Assert.Equal("fresh", await call);
		Assert.Contains(logger.Messages, message => message.Contains("dropped", StringComparison.Ordinal) && message.Contains(expected, StringComparison.Ordinal));
	}

	private static RunningInventor Inventor(DateTime? startTime) => new(1234, null, startTime, true, 2025);

	private sealed class RecordingLogger : ILogger<BridgeClient>
	{
		public List<string> Messages { get; } = [];

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
			Messages.Add(formatter(state, exception));
	}
}
