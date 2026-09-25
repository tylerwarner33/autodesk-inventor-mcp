using System.IO.Pipes;
using System.Text;
using System.Text.Json;

using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	Takes the place of the add-in: hosts a pipe with a unique name, and answers what the test tells it to.
/// </summary>
internal sealed class TestPipeServer : IAsyncDisposable
{
	private readonly List<TestConnection> _connections = [];

	public string PipeName { get; } = $"InventorMcp.Test.{Guid.NewGuid():N}";

	/// <summary>
	/// 	Waits for the next client connection.
	/// </summary>
	public async Task<TestConnection> AcceptAsync()
	{
		NamedPipeServerStream pipe = new(PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
		await pipe.WaitForConnectionAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

		TestConnection connection = new(pipe);
		_connections.Add(connection);

		return connection;
	}

	public async ValueTask DisposeAsync()
	{
		foreach (TestConnection connection in _connections)
			await connection.DisposeAsync();
	}
}

/// <summary>
/// 	One connection of <see cref="TestPipeServer"/>.
/// </summary>
internal sealed class TestConnection(NamedPipeServerStream pipe) : IAsyncDisposable
{
	private readonly StreamReader _reader = new(pipe, new UTF8Encoding(false), false, leaveOpen: true);
	private readonly StreamWriter _writer = new(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

	public async Task<BridgeRequest> ReadRequestAsync(TimeSpan? timeout = null)
	{
		string? line = await _reader.ReadLineAsync(TestContext.Current.CancellationToken)
			.AsTask()
			.WaitAsync(timeout ?? TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

		return JsonSerializer.Deserialize<BridgeRequest>(line!, BridgeProtocol.SerializerOptions)!;
	}

	public Task RespondAsync(string id, string result) =>
		_writer.WriteLineAsync(JsonSerializer.Serialize(
			new BridgeResponse(id, true, JsonSerializer.SerializeToElement(result), null),
			BridgeProtocol.SerializerOptions));

	public void Disconnect() => pipe.Disconnect();

	public async ValueTask DisposeAsync()
	{
		try
		{
			await _writer.DisposeAsync();
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException)
		{
			// The flush fails on a pipe that the test disconnected.
		}

		_reader.Dispose();
		await pipe.DisposeAsync();
	}
}

/// <summary>
/// 	A detector whose state the test sets.
/// </summary>
internal sealed class FakeDialogs : IBlockingDialogs
{
	private int _detectCalls;
	private int _clickCalls;

	/// <summary>
	/// 	The dialogs that block, or null for no block.
	/// </summary>
	public volatile DialogSnapshot[]? Dialogs;

	/// <summary>
	/// 	True for a block with no dialog of the process.
	/// </summary>
	public volatile bool BlockedWithoutDialog;

	public int DetectCalls => _detectCalls;

	public int ClickCalls => _clickCalls;

	public BlockState Detect(int processId)
	{
		_ = Interlocked.Increment(ref _detectCalls);

		DialogSnapshot[]? dialogs = Dialogs;

		return BlockedWithoutDialog
			? new BlockState(processId, true, true, [])
			: new BlockState(processId, dialogs is not null, true, dialogs ?? []);
	}

	/// <summary>
	/// 	Clicks, and the dialog closes.
	/// </summary>
	public ClickOutcome TryClick(int processId, DialogSnapshot dialog, string buttonName)
	{
		_ = Interlocked.Increment(ref _clickCalls);
		Dialogs = null;

		return new ClickOutcome(ClickStatus.Clicked, $"Clicked '{buttonName}'.");
	}
}

/// <summary>
/// 	Builds a <see cref="BridgeClient"/> against a test pipe, with a fake detector and fake time.
/// </summary>
internal sealed class TestBridge : IAsyncDisposable
{
	public TestPipeServer Server { get; } = new();

	public FakeDialogs Dialogs { get; } = new();

	public FakeTimeProvider Time { get; } = new();

	public BridgeClient Client { get; }

	public TestBridge() => Client = new BridgeClient(NullLogger<BridgeClient>.Instance, Dialogs, Time, Server.PipeName);

	/// <summary>
	/// 	Starts a ping, and returns it with the request that the fake add-in received.
	/// </summary>
	public async Task<(Task<string> Call, TestConnection Connection, BridgeRequest Request)> StartPingAsync(TestConnection? connection = null)
	{
		Task<TestConnection> accept = connection is null ? Server.AcceptAsync() : Task.FromResult(connection);
		Task<string> call = Client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken);
		TestConnection accepted = await accept;

		return (call, accepted, await accepted.ReadRequestAsync());
	}

	/// <summary>
	/// 	Moves fake time forward in small steps, and lets the watchdog run after each step.
	/// </summary>
	public async Task AdvanceAsync(TimeSpan total)
	{
		TimeSpan step = TimeSpan.FromMilliseconds(250);

		for (TimeSpan moved = TimeSpan.Zero; moved < total; moved += step)
		{
			Time.Advance(step);
			await Task.Delay(30, TestContext.Current.CancellationToken);
		}
	}

	/// <summary>
	/// 	Waits in real time for a condition that a background task sets.
	/// </summary>
	public static async Task WaitUntilAsync(Func<bool> condition)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(10);

		while (condition() is false)
		{
			if (DateTime.UtcNow > deadline)
				throw new TimeoutException("The condition did not become true within 10 s.");

			await Task.Delay(20, TestContext.Current.CancellationToken);
		}
	}

	public async ValueTask DisposeAsync()
	{
		await Client.DisposeAsync();
		await Server.DisposeAsync();
	}
}