using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	Which release a session talks to, with fake pipes that carry release names.
/// </summary>
[Trait("Level", "Unit")]
public sealed class ReleaseSelectionTests
{
	private const string _prefix = "InventorMcp.Test.Selection";

	private static ReleaseSelection Selection(string environmentValue = "", params string[] pipes) =>
		new(_prefix, environmentValue, () => pipes);

	// The test process hosts the fake pipes, so the host check accepts it.
	private static BridgeClient ClientFor(ReleaseSelection selection) =>
		new(NullLogger<BridgeClient>.Instance, new FakeDialogs(), new FakeTimeProvider(), selection, checkHost: static _ => null);

	[Fact]
	public void NoChoiceAndOnePipeUsesThatPipe()
	{
		PipeResolution resolution = Selection(pipes: $"{_prefix}.2026").Resolve();

		Assert.Equal($"{_prefix}.2026", resolution.PipeName);
		Assert.Equal(2026, resolution.ReleaseYear);
	}

	[Fact]
	public void NoChoiceAndTwoPipesRequiresAChoice()
	{
		PipeResolution resolution = Selection(pipes: [$"{_prefix}.2025", $"{_prefix}.2027"]).Resolve();

		Assert.Equal(BridgeErrorCodes.ReleaseRequired, resolution.ErrorCode);
		Assert.Contains("2025", resolution.Message);
		Assert.Contains("2027", resolution.Message);
	}

	[Fact]
	public void EnvironmentVariableWinsOverAnotherPipe()
	{
		PipeResolution resolution = Selection("2027", $"{_prefix}.2025", $"{_prefix}.2027").Resolve();

		Assert.Equal($"{_prefix}.2027", resolution.PipeName);
	}

	[Fact]
	public void ToolChoiceWinsOverTheEnvironmentVariable()
	{
		ReleaseSelection selection = Selection("2027");

		selection.Choose(2025);

		Assert.Equal(2025, selection.Chosen);
		Assert.Equal("chosen", selection.Source);

		selection.Choose(null);

		Assert.Equal(2027, selection.Chosen);
		Assert.Equal("environment", selection.Source);
	}

	[Fact]
	public void ChosenReleaseNeverFallsBackToAnotherPipe()
	{
		ReleaseSelection selection = Selection(pipes: $"{_prefix}.2025");

		selection.Choose(2026);

		Assert.Equal($"{_prefix}.2026", selection.Resolve().PipeName);
	}

	[Fact]
	public void EnvironmentValueThatIsNotAReleaseIsReported()
	{
		PipeResolution resolution = Selection("2019", $"{_prefix}.2025").Resolve();

		Assert.Equal(BridgeErrorCodes.ReleaseRequired, resolution.ErrorCode);
		Assert.Contains("2019", resolution.Message);
	}

	[Fact]
	public void OnlyTheLegacyPipeIsOutdated() =>
		Assert.Equal(BridgeErrorCodes.BridgeOutdated, Selection(pipes: _prefix).Resolve().ErrorCode);

	[Fact]
	public void NoPipeIsNotRunning() => Assert.Equal(BridgeErrorCodes.NotRunning, Selection().Resolve().ErrorCode);

	[Fact]
	public void GenerationChangesOnlyWhenTheReleaseChanges()
	{
		ReleaseSelection selection = Selection();

		selection.Choose(2025);
		selection.Choose(2025);

		Assert.Equal(1, selection.Generation);

		selection.Choose(null);

		Assert.Equal(2, selection.Generation);
	}

	[Fact]
	public async Task SystemPipesAreListedByRelease()
	{
		await using TestPipeServer older = new(2025);
		await using TestPipeServer newer = new(2027, older.Prefix);
		older.Listen();
		newer.Listen();

		ReleaseSelection selection = new(older.Prefix, string.Empty);

		Assert.Equal([2025, 2027], selection.RunningReleases());
		Assert.Equal(BridgeErrorCodes.ReleaseRequired, selection.Resolve().ErrorCode);
	}

	[Fact]
	public async Task LegacyPipeOnTheSystemIsOutdated()
	{
		await using TestPipeServer legacy = new(null);
		legacy.Listen();

		Assert.Equal(BridgeErrorCodes.BridgeOutdated, new ReleaseSelection(legacy.Prefix, string.Empty).Resolve().ErrorCode);
	}

	[Fact]
	public async Task NoChoiceAndOnePipeConnectsToIt()
	{
		await using TestPipeServer server = new(2026);
		server.Listen();
		await using BridgeClient client = ClientFor(new ReleaseSelection(server.Prefix, string.Empty));

		Task<TestConnection> accept = server.AcceptAsync();

		Assert.True(await client.TryConnectAsync(TestContext.Current.CancellationToken));
		_ = await accept;

		Assert.Equal(2026, client.ReleaseYear);
	}

	[Fact]
	public async Task NoChoiceAndTwoPipesFailsWithReleaseRequired()
	{
		await using TestPipeServer older = new(2025);
		await using TestPipeServer newer = new(2027, older.Prefix);
		older.Listen();
		newer.Listen();
		await using BridgeClient client = ClientFor(new ReleaseSelection(older.Prefix, string.Empty));

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(
			() => client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken));

		Assert.Equal(BridgeErrorCodes.ReleaseRequired, exception.Code);
	}

	[Fact]
	public async Task EnvironmentVariableConnectsToThatReleaseWhenAnotherPipeExists()
	{
		await using TestPipeServer older = new(2025);
		await using TestPipeServer newer = new(2027, older.Prefix);
		older.Listen();
		newer.Listen();
		await using BridgeClient client = ClientFor(new ReleaseSelection(older.Prefix, "2027"));

		Task<TestConnection> accept = newer.AcceptAsync();

		Assert.True(await client.TryConnectAsync(TestContext.Current.CancellationToken));
		_ = await accept;

		Assert.Equal(2027, client.ReleaseYear);
	}

	[Fact]
	public async Task ChosenReleaseWithNoPipeNamesTheReleaseAndDoesNotFallBack()
	{
		await using TestPipeServer other = new(2025);
		other.Listen();
		ReleaseSelection selection = new(other.Prefix, string.Empty);
		selection.Choose(2026);
		await using BridgeClient client = ClientFor(selection);

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(
			() => client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken));

		Assert.Equal(BridgeErrorCodes.NotRunning, exception.Code);
		Assert.Contains("2026", exception.Message);
	}

	[Fact]
	public async Task ChangeOfTheChoiceConnectsToTheNewPipe()
	{
		await using TestPipeServer older = new(2025);
		await using TestPipeServer newer = new(2027, older.Prefix);
		ReleaseSelection selection = new(older.Prefix, string.Empty);
		selection.Choose(2025);
		await using BridgeClient client = ClientFor(selection);

		Task<TestConnection> firstAccept = older.AcceptAsync();
		Assert.True(await client.TryConnectAsync(TestContext.Current.CancellationToken));
		_ = await firstAccept;

		Assert.Equal(2025, client.ReleaseYear);

		selection.Choose(2027);
		Task<TestConnection> secondAccept = newer.AcceptAsync();

		Assert.True(await client.TryConnectAsync(TestContext.Current.CancellationToken));
		_ = await secondAccept;

		Assert.Equal(2027, client.ReleaseYear);
	}

	[Fact]
	public async Task AutomaticSessionDoesNotMoveToAnotherReleaseWhenItsReleaseCloses()
	{
		TestPipeServer older = new(2025);
		await using TestPipeServer newer = new(2027, older.Prefix);
		older.Listen();
		await using BridgeClient client = ClientFor(new ReleaseSelection(older.Prefix, string.Empty));

		Task<TestConnection> accept = older.AcceptAsync();
		Assert.True(await client.TryConnectAsync(TestContext.Current.CancellationToken));
		_ = await accept;

		// Release 2025 closes, and 2027 is now the only pipe.
		await older.DisposeAsync();
		newer.Listen();

		// A call that reached 2027 would wait forever for an answer, so the wait is limited.
		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(
			() => client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

		Assert.Equal(BridgeErrorCodes.ReleaseRequired, exception.Code);
		Assert.Contains("2025", exception.Message);
		Assert.Contains("2027", exception.Message);
	}

	[Fact]
	public async Task AutomaticSessionReconnectsToTheSameReleaseAfterARestart()
	{
		TestPipeServer first = new(2025);
		first.Listen();
		await using BridgeClient client = ClientFor(new ReleaseSelection(first.Prefix, string.Empty));

		Task<TestConnection> accept = first.AcceptAsync();
		Assert.True(await client.TryConnectAsync(TestContext.Current.CancellationToken));
		_ = await accept;

		await first.DisposeAsync();
		await using TestPipeServer restarted = new(2025, first.Prefix);
		restarted.Listen();

		// The dropped pipe shows only on use, so a call (not a connect) makes the client reconnect.
		Task<TestConnection> acceptAgain = restarted.AcceptAsync();
		Task<string> call = client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken);
		TestConnection connection = await acceptAgain;
		BridgeRequest request = await connection.ReadRequestAsync();
		await connection.RespondAsync(request.Id, "pong");

		Assert.Equal("pong", await call.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
		Assert.Equal(2025, client.ReleaseYear);
	}

	[Fact]
	public async Task OnlyTheLegacyPipeFailsWithBridgeOutdated()
	{
		await using TestPipeServer legacy = new(null);
		legacy.Listen();
		await using BridgeClient client = ClientFor(new ReleaseSelection(legacy.Prefix, string.Empty));

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(
			() => client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken));

		Assert.Equal(BridgeErrorCodes.BridgeOutdated, exception.Code);
	}
}
