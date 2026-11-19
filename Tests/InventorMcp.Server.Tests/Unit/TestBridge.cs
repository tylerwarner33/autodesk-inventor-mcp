using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	Takes the place of the add-in: hosts a pipe with a unique name, and answers what the test tells it to.
/// </summary>
/// <remarks>
/// 	The name is <c>&lt;prefix&gt;.&lt;year&gt;</c> like a release pipe, with a prefix that no real add-in uses.
/// 	Several servers of one test share the prefix, to play several Inventor releases.
/// </remarks>
/// <param name="releaseYear">
/// 	The release the pipe belongs to. Null gives the legacy name, which is the prefix alone.
/// </param>
/// <param name="prefix">
/// 	The start of the pipe name, or null for a new unique prefix.
/// </param>
internal sealed class TestPipeServer(int? releaseYear = 2025, string? prefix = null) : IAsyncDisposable
{
	private readonly List<TestConnection> _connections = [];
	private NamedPipeServerStream? _listening;

	public string Prefix { get; } = prefix ?? $"InventorMcp.Test.{Guid.NewGuid():N}";

	public string PipeName => releaseYear is int year ? BridgeProtocol.PipeNameFor(year, Environment.ProcessId, Prefix) : Prefix;

	/// <summary>
	/// 	Creates the pipe now, so that it exists before the client looks for it.
	/// </summary>
	public void Listen() =>
		_listening ??= new NamedPipeServerStream(PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

	/// <summary>
	/// 	Waits for the next client connection.
	/// </summary>
	public async Task<TestConnection> AcceptAsync()
	{
		NamedPipeServerStream pipe = _listening ?? new(PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
		_listening = null;
		await pipe.WaitForConnectionAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

		TestConnection connection = new(pipe);
		_connections.Add(connection);

		return connection;
	}

	public async ValueTask DisposeAsync()
	{
		if (_listening is not null)
			await _listening.DisposeAsync();

		foreach (TestConnection connection in _connections)
			await connection.DisposeAsync();
	}
}

/// <summary>
/// 	A request that the fake add-in received.
/// </summary>
/// <param name="Id">
/// 	The JSON-RPC id, to answer it with <see cref="TestConnection.RespondAsync"/>.
/// </param>
/// <param name="Operation">
/// 	The method.
/// </param>
/// <param name="Payload">
/// 	The params without the member that the MCP client adds, or null.
/// </param>
internal sealed record TestRequest(string Id, string Operation, JsonElement? Payload);

/// <summary>
/// 	One connection of <see cref="TestPipeServer"/>: an MCP session of the SDK whose requests the test answers.
/// </summary>
internal sealed class TestConnection : IAsyncDisposable
{
	private static readonly string[] _operations =
	[
		.. typeof(BridgeOperations)
			.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(static field => field.IsLiteral)
			.Select(static field => (string)field.GetRawConstantValue()!)
	];

	private readonly NamedPipeServerStream _pipe;
	private readonly Channel<TestRequest> _requests = Channel.CreateUnbounded<TestRequest>();
	private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _answers = new(StringComparer.Ordinal);
	private readonly McpServer _server;
	private readonly Task _run;

	public TestConnection(NamedPipeServerStream pipe)
	{
		_pipe = pipe;

		McpServerOptions options = new()
		{
			ServerInfo = new Implementation { Name = "InventorMcp.TestAddIn", Version = "test" },
			ProtocolVersion = BridgeProtocol.McpVersion,
			RequestHandlers = [.. _operations.Select(operation => new McpServerRequestHandler { Method = operation, Handler = (context, cancellationToken) => ReceiveAsync(operation, context, cancellationToken) })]
		};

		_server = McpServer.Create(new StreamServerTransport(pipe, pipe, "InventorMcp.TestAddIn", NullLoggerFactory.Instance), options, NullLoggerFactory.Instance);
		_run = _server.RunAsync(CancellationToken.None);
	}

	/// <summary>
	/// 	The ids of the requests whose cancel reached the fake add-in.
	/// </summary>
	public ConcurrentQueue<string> Cancelled { get; } = [];

	public async Task<TestRequest> ReadRequestAsync(TimeSpan? timeout = null) =>
		await _requests.Reader.ReadAsync(TestContext.Current.CancellationToken)
			.AsTask()
			.WaitAsync(timeout ?? TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

	public Task RespondAsync(string id, object? result)
	{
		_ = _answers[id].TrySetResult(BridgeResult.Wrap(result));

		return Task.CompletedTask;
	}

	/// <summary>
	/// 	Answers a request with a bridge failure, as the add-in does.
	/// </summary>
	public Task FailAsync(string id, string code, string message)
	{
		McpProtocolException failure = new(message, (McpErrorCode)BridgeResult.ServerErrorCode);
		failure.Data[BridgeResult.CodeMember] = code;
		failure.Data[BridgeResult.MessageMember] = message;
		_ = _answers[id].TrySetException(failure);

		return Task.CompletedTask;
	}

	public void Disconnect() => _pipe.Disconnect();

	public async ValueTask DisposeAsync()
	{
		foreach (TaskCompletionSource<JsonNode?> answer in _answers.Values)
			_ = answer.TrySetCanceled(TestContext.Current.CancellationToken);

		try
		{
			await _server.DisposeAsync();
			await _run;
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
		{
			// The session of a pipe that the test disconnected ends with an error.
		}

		await _pipe.DisposeAsync();
	}

	private async ValueTask<JsonNode?> ReceiveAsync(string operation, RequestContext<JsonNode?> context, CancellationToken cancellationToken)
	{
		string id = ((JsonRpcRequest)context.JsonRpcMessage).Id.ToString();
		TaskCompletionSource<JsonNode?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_answers[id] = answer;

		JsonNode? payload = context.Params?.DeepClone();
		_ = (payload as JsonObject)?.Remove("_meta");

		_ = _requests.Writer.TryWrite(new TestRequest(id, operation, payload is JsonObject { Count: 0 } or null ? null : JsonSerializer.SerializeToElement(payload)));

		await using (cancellationToken.Register(() =>
		{
			Cancelled.Enqueue(id);
			_ = answer.TrySetCanceled(cancellationToken);
		}))
		{
			return await answer.Task;
		}
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

	public ReleaseSelection Selection { get; }

	/// <summary>
	/// 	Chooses release 2025, which is the release of <see cref="Server"/>, so a call waits for the pipe to appear.
	/// </summary>
	/// <param name="checkHost">
	/// 	The check of the process that hosts the pipe, or null to accept the test process, which is not Inventor.
	/// </param>
	/// <param name="running">
	/// 	The Inventor processes that the process list gives, or null for none, so an Inventor on the test machine is
	/// 	never found.
	/// </param>
	/// <param name="processRuns">
	/// 	Whether the process that hosts the pipe still runs, or null for always, because the host is the test process.
	/// </param>
	/// <param name="isAddInDeployed">
	/// 	Tells if the add-in is deployed, or null for deployed.
	/// </param>
	/// <param name="logger">
	/// 	The log of the client, or null for no log.
	/// </param>
	/// <param name="findRunning">
	/// 	The process list as a function, for a test that needs the time of the fake clock. It wins over <paramref name="running"/>.
	/// </param>
	public TestBridge(
		Func<int, string?>? checkHost = null,
		IReadOnlyList<RunningInventor>? running = null,
		Func<int, bool>? processRuns = null,
		Func<int, bool>? isAddInDeployed = null,
		ILogger<BridgeClient>? logger = null,
		Func<int?, IReadOnlyList<RunningInventor>>? findRunning = null)
	{
		Selection = new ReleaseSelection(Server.Prefix, environmentValue: string.Empty);
		Selection.Choose(2025);
		// The defaults only, so a user's own settings file does not change the tests.
		Client = new BridgeClient(
			logger ?? NullLogger<BridgeClient>.Instance,
			Dialogs,
			Time,
			Selection,
			static () => DialogSettings.Defaults,
			checkHost ?? (static _ => null),
			findRunning ?? (_ => running ?? []),
			isAddInDeployed ?? (static _ => true),
			processRuns ?? (static _ => true));
	}

	/// <summary>
	/// 	Starts a call that uses the main thread, and returns it with the request that the fake add-in received.
	/// </summary>
	/// <param name="connection">
	/// 	The connection of an earlier call, or null to accept a new one.
	/// </param>
	/// <param name="cancellationToken">
	/// 	The token of the call, or none for the token of the test.
	/// </param>
	public async Task<(Task<string> Call, TestConnection Connection, TestRequest Request)> StartCallAsync(TestConnection? connection = null, CancellationToken? cancellationToken = null)
	{
		Task<TestConnection> accept = connection is null ? Server.AcceptAsync() : Task.FromResult(connection);
		Task<string> call = Client.InvokeAsync<string>(BridgeOperations.Session, null, cancellationToken ?? TestContext.Current.CancellationToken);
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
