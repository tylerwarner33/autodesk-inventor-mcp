using System.Diagnostics;
using System.IO.Pipes;

using InventorMcp.Server.Bridge;
using InventorMcp.Server.McpTools;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using ModelContextProtocol;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The wait of <c>inventor_session</c> for a bridge that does not answer yet.
/// </summary>
[Trait("Level", "Unit")]
public sealed class WaitForAnswerTests
{
	private static readonly Progress<ProgressNotificationValue> _noProgress = new();

	// The test process hosts the pipes, so the host check accepts it.
	private static BridgeClient ClientFor(string prefix, int releaseYear) =>
		new(
			NullLogger<BridgeClient>.Instance,
			new FakeDialogs(),
			new FakeTimeProvider(),
			new ReleaseSelection(prefix, string.Empty, () => [$"{prefix}.{releaseYear}"]),
			checkHost: static _ => null);

	[Fact]
	public async Task WaitEndsAtItsLimitOnABusyPipe()
	{
		// One instance that another client holds: a connection attempt waits for the bridge's full connect timeout.
		string prefix = $"InventorMcp.Test.{Guid.NewGuid():N}";
		await using NamedPipeServerStream server = new($"{prefix}.2026", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
		await using NamedPipeClientStream holder = new(".", $"{prefix}.2026", PipeDirection.InOut, PipeOptions.Asynchronous);
		Task accept = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
		await holder.ConnectAsync(5000, TestContext.Current.CancellationToken);
		await accept;
		await using BridgeClient client = ClientFor(prefix, 2026);

		Stopwatch stopwatch = Stopwatch.StartNew();
		await InventorTool.WaitForAnswerAsync(client, TimeSpan.FromSeconds(1), _noProgress, TestContext.Current.CancellationToken);

		Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(1.6));
		Assert.Null(client.InventorProcessId);
	}

	[Fact]
	public async Task WaitEndsWhenTheBridgeAnswers()
	{
		await using TestPipeServer server = new(2026);
		server.Listen();
		await using BridgeClient client = ClientFor(server.Prefix, 2026);
		Task<TestConnection> accept = server.AcceptAsync();

		Stopwatch stopwatch = Stopwatch.StartNew();
		await InventorTool.WaitForAnswerAsync(client, TimeSpan.FromSeconds(10), _noProgress, TestContext.Current.CancellationToken);
		_ = await accept;

		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
		Assert.Equal(2026, client.ReleaseYear);
	}

	[Fact]
	public async Task NoWaitMakesNoAttempt()
	{
		// The pipe would answer, so a connection would prove that an attempt ran.
		await using TestPipeServer server = new(2026);
		server.Listen();
		await using BridgeClient client = ClientFor(server.Prefix, 2026);

		await InventorTool.WaitForAnswerAsync(client, TimeSpan.Zero, _noProgress, TestContext.Current.CancellationToken);

		Assert.Null(client.ReleaseYear);
	}
}
