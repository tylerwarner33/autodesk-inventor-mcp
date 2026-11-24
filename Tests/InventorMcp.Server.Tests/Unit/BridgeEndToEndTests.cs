using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

using InventorMcp.AddIn.Bridge;
using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The real add-in listener against the real client, with no Inventor.
/// </summary>
/// <remarks>
/// 	The test project compiles <c>BridgeServer.cs</c> and <c>OperationDispatcher.cs</c> of the add-in. They use no Inventor
/// 	type, so the two ends of the pipe are tested together, which a fake listener does not do.
/// 	Several tests also check a behavior of the MCP SDK that the bridge depends on, so an SDK update that changes it fails
/// 	here. See <c>Docs/Research/Mcp-Over-Pipe-Spike.md</c>, "SDK behavior to check again on each SDK update".
/// </remarks>
[Trait("Level", "Unit")]
public sealed class BridgeEndToEndTests
{
	[Fact]
	public async Task TheRealServerAndTheRealClientTalkOnTheNameOfTheProcess()
	{
		await using Listener listener = await Listener.StartAsync();
		await using BridgeClient client = listener.Client();

		string answer = await client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken);

		Assert.Equal("pong", answer);
		Assert.Equal(Environment.ProcessId, client.InventorProcessId);
		Assert.Equal(2026, client.ReleaseYear);
		Assert.Empty(listener.Log);
	}

	[Fact]
	public async Task ASecondListenerOnTheSameNameCannotClaimIt()
	{
		await using Listener listener = await Listener.StartAsync();

		// A program that takes the name of this process cannot become a listener of it: FirstPipeInstance refuses.
		using BridgeServer second = new(listener.Dispatcher, listener.Write, listener.PipeName);
		second.Start();
		await TestBridge.WaitUntilAsync(() => listener.Log.Any(static line => line.Contains("Could not claim the pipe", StringComparison.Ordinal)));

		await using BridgeClient client = listener.Client();

		Assert.Equal("pong", await client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task TheListenerServesManyClientsAtOnce()
	{
		await using Listener listener = await Listener.StartAsync();
		BridgeClient[] clients = [.. Enumerable.Range(0, 4).Select(_ => listener.Client())];

		try
		{
			string[] answers = await Task.WhenAll(clients.Select(client => client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken)));

			Assert.All(answers, static answer => Assert.Equal("pong", answer));
		}
		finally
		{
			foreach (BridgeClient client in clients)
				await client.DisposeAsync();
		}
	}

	[Fact]
	public async Task ThePipeRefusesAClientFromAnotherComputer()
	{
		await using Listener listener = await Listener.StartAsync();

		// A pipe that is connected has the ACL of the server, and a deny entry for the NETWORK SID is part of it.
		await using NamedPipeClientStream client = new(".", listener.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		await client.ConnectAsync(5_000, TestContext.Current.CancellationToken);

		PipeSecurity security = client.GetAccessControl();
		SecurityIdentifier network = new(WellKnownSidType.NetworkSid, null);

		Assert.Contains(
			security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>(),
			rule => rule.AccessControlType == AccessControlType.Deny && rule.IdentityReference.Equals(network));
	}

	[Fact]
	public async Task ANullResultCompletesTheCall()
	{
		// SDK 2.4.0 sends a null result with no "result" member, and its client then never completes the call.
		// The result object of BridgeResult keeps that away. When the SDK is fixed, this still passes.
		await using Listener listener = await Listener.StartAsync(static dispatcher =>
			dispatcher.Register(BridgeOperations.Update, static (_, _) => Task.FromResult<object?>(null)));
		await using BridgeClient client = listener.Client();

		string? answer = await client.InvokeAsync<string?>(BridgeOperations.Update, null, TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

		Assert.Null(answer);
	}

	[Fact]
	public async Task AFailureKeepsItsCodeItsMessageAndItsDetail()
	{
		await using Listener listener = await Listener.StartAsync(static dispatcher =>
			dispatcher.Register(BridgeOperations.Session, static (_, _) => throw new BridgeFailureException(BridgeErrorCodes.Busy, "Inventor is busy.", "RPC_E_CALL_REJECTED")));
		await using BridgeClient client = listener.Client();

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(
			() => client.InvokeAsync<string>(BridgeOperations.Session, null, TestContext.Current.CancellationToken));

		Assert.Equal(BridgeErrorCodes.Busy, exception.Code);
		Assert.Equal("Inventor is busy.", exception.Message);
		Assert.Equal("RPC_E_CALL_REJECTED", exception.Detail);
	}

	[Fact]
	public async Task AnOperationThatTheAddInDoesNotKnowAsksForADeployment()
	{
		await using Listener listener = await Listener.StartAsync();
		await using BridgeClient client = listener.Client();

		InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(
			() => client.InvokeAsync<string>(BridgeOperations.Health, null, TestContext.Current.CancellationToken));

		Assert.Equal(BridgeErrorCodes.UnknownOperation, exception.Code);
		Assert.Contains("deploy the add-in again", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TheAddInRunsTheRequestsOfOneConnectionOneAtATime()
	{
		int running = 0;
		int mostAtOnce = 0;

		await using Listener listener = await Listener.StartAsync(dispatcher => dispatcher.Register(BridgeOperations.Session, async (_, _) =>
		{
			int now = Interlocked.Increment(ref running);
			InterlockedMax(ref mostAtOnce, now);
			await Task.Delay(100, TestContext.Current.CancellationToken);
			_ = Interlocked.Decrement(ref running);

			return "done";
		}));

		// The SDK runs the requests of one connection at the same time, so the queue of the dispatcher must hold them.
		await using McpClient raw = await listener.RawClientAsync();
		_ = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => raw.SendRequestAsync(new JsonRpcRequest { Method = BridgeOperations.Session }, TestContext.Current.CancellationToken)));

		Assert.Equal(1, mostAtOnce);
	}

	[Fact]
	public async Task AnOperationWithoutTheMainThreadSkipsTheQueue()
	{
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

		await using Listener listener = await Listener.StartAsync(dispatcher =>
		{
			dispatcher.Register(BridgeOperations.Session, async (_, _) =>
			{
				await release.Task;
				return "done";
			});
			dispatcher.RegisterWithoutMainThread(BridgeOperations.Activity, static _ => Task.FromResult<object?>("events"));
		});

		await using McpClient raw = await listener.RawClientAsync();
		Task<JsonRpcResponse> blocked = raw.SendRequestAsync(new JsonRpcRequest { Method = BridgeOperations.Session }, TestContext.Current.CancellationToken);

		JsonRpcResponse activity = await raw.SendRequestAsync(new JsonRpcRequest { Method = BridgeOperations.Activity }, TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

		Assert.Equal("events", BridgeResult.Unwrap<string>(activity.Result));
		Assert.False(blocked.IsCompleted);

		release.SetResult();
		_ = await blocked;
	}

	[Fact]
	public void EachOperationIsRegisteredWithTheMethodOfItsThread()
	{
		OperationDispatcher dispatcher = new(static _ => { });

		_ = Assert.Throws<ArgumentException>(() => dispatcher.Register(BridgeOperations.Activity, static (_, _) => Task.FromResult<object?>(null)));
		_ = Assert.Throws<ArgumentException>(() => dispatcher.RegisterWithoutMainThread(BridgeOperations.Session, static _ => Task.FromResult<object?>(null)));
	}

	[Fact]
	public async Task ACancelReachesARunningHandler()
	{
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);

		await using Listener listener = await Listener.StartAsync(dispatcher => dispatcher.Register(BridgeOperations.Session, async (_, cancellationToken) =>
		{
			await using (cancellationToken.Register(canceled.SetResult))
			{
				started.SetResult();
				await Task.Delay(Timeout.Infinite, cancellationToken);
			}

			return null;
		}));
		await using BridgeClient client = listener.Client();
		using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

		Task<string> call = client.InvokeAsync<string>(BridgeOperations.Session, null, cancel.Token);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
		await cancel.CancelAsync();

		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
		await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
	}

	[Fact]
	public async Task ACanceledRequestThatWaitsInTheQueueNeverRuns()
	{
		TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int secondRuns = 0;

		await using Listener listener = await Listener.StartAsync(dispatcher => dispatcher.Register(BridgeOperations.Session, async (payload, cancellationToken) =>
		{
			if (payload?.GetProperty("name").GetString() == "second")
			{
				_ = Interlocked.Increment(ref secondRuns);
				return "second";
			}

			firstStarted.SetResult();
			await releaseFirst.Task.WaitAsync(cancellationToken);
			return "first";
		}));

		await using McpClient raw = await listener.RawClientAsync();
		using CancellationTokenSource cancelSecond = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

		Task<JsonRpcResponse> first = raw.SendRequestAsync(Request("first"), TestContext.Current.CancellationToken);
		await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
		Task<JsonRpcResponse> second = raw.SendRequestAsync(Request("second"), cancelSecond.Token);

		// The SDK does not send a request whose token is already canceled, so the second request must reach the queue
		// first. Nothing reports that it waits there, and a local pipe takes far less than this.
		await Task.Delay(500, TestContext.Current.CancellationToken);
		await cancelSecond.CancelAsync();
		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

		// The dispatcher logs the cancel when it reaches the queue, so the first request is released only after it.
		await TestBridge.WaitUntilAsync(() => listener.Log.Any(static line => line.Contains("canceled before it started", StringComparison.Ordinal)));
		releaseFirst.SetResult();
		_ = await first;

		Assert.Equal(0, secondRuns);

		static JsonRpcRequest Request(string name) => new() { Method = BridgeOperations.Session, Params = new JsonObject { ["name"] = name } };
	}

	[Fact]
	public async Task ACallEndsWhenTheAddInStops()
	{
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

		Listener listener = await Listener.StartAsync(dispatcher => dispatcher.Register(BridgeOperations.Session, async (_, cancellationToken) =>
		{
			started.SetResult();
			await Task.Delay(Timeout.Infinite, cancellationToken);
			return null;
		}));
		await using BridgeClient client = listener.Client();

		Task<string> call = client.InvokeAsync<string>(BridgeOperations.Session, null, TestContext.Current.CancellationToken);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
		await listener.DisposeAsync();

		// The dropped pipe ends the wait, and the retry finds no pipe.
		_ = await Assert.ThrowsAsync<InventorBridgeException>(() => call.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task TheHandshakeDoesNotWaitForABusyAddIn()
	{
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

		await using Listener listener = await Listener.StartAsync(dispatcher => dispatcher.Register(BridgeOperations.Session, async (_, _) =>
		{
			started.SetResult();
			await release.Task;
			return "done";
		}));
		await using BridgeClient busy = listener.Client();
		await using BridgeClient second = listener.Client();

		Task<string> call = busy.InvokeAsync<string>(BridgeOperations.Session, null, TestContext.Current.CancellationToken);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

		// In Inventor, the handshake answers while a dialog holds the main thread.
		Assert.True(await second.TryConnectAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

		release.SetResult();
		Assert.Equal("done", await call);
	}

	[Fact]
	public async Task TheAddInRefusesAnotherMcpVersion()
	{
		await using Listener listener = await Listener.StartAsync();

		_ = await Assert.ThrowsAnyAsync<Exception>(() => listener.RawClientAsync("2025-11-25"));
	}

	[Fact]
	public async Task InvalidJsonOnThePipeDoesNotEndTheConnection()
	{
		await using Listener listener = await Listener.StartAsync();
		NamedPipeClientStream pipe = new(".", listener.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		await pipe.ConnectAsync(5_000, TestContext.Current.CancellationToken);

		// Text that is not JSON, and a JSON-RPC batch, before the session starts on the same connection.
		await pipe.WriteAsync(Encoding.UTF8.GetBytes("not json\n[]\n"), TestContext.Current.CancellationToken);

		await using McpClient raw = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe, NullLoggerFactory.Instance), Listener.ClientOptions(BridgeProtocol.McpVersion), NullLoggerFactory.Instance, TestContext.Current.CancellationToken);
		JsonRpcResponse response = await raw.SendRequestAsync(new JsonRpcRequest { Method = BridgeOperations.Ping }, TestContext.Current.CancellationToken);

		Assert.Equal("pong", BridgeResult.Unwrap<string>(response.Result));
	}

	[Fact]
	public async Task TheSeventeenthClientGetsConnectionsInUse()
	{
		await using Listener listener = await Listener.StartAsync();
		List<McpClient> held = [];

		try
		{
			for (int index = 0; index < 16; index++)
				held.Add(await listener.RawClientAsync());

			await using BridgeClient seventeenth = listener.Client();

			InventorBridgeException exception = await Assert.ThrowsAsync<InventorBridgeException>(
				() => seventeenth.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken));

			Assert.Equal(BridgeErrorCodes.ConnectionsInUse, exception.Code);
			Assert.DoesNotContain(listener.Log, static line => line.Contains("accept failed", StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			foreach (McpClient client in held)
				await client.DisposeAsync();
		}
	}

	private static void InterlockedMax(ref int target, int value)
	{
		int current;

		while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
		{
			// Another thread changed the value, so read it again.
		}
	}

	/// <summary>
	/// 	A real <see cref="BridgeServer"/> on a name of release 2026 and this process, with a <c>ping</c> handler.
	/// </summary>
	private sealed class Listener : IAsyncDisposable
	{
		private readonly List<string> _log = [];
		private readonly BridgeServer _server;
		private bool _disposed;

		private Listener(Action<OperationDispatcher>? register)
		{
			Prefix = $"InventorMcp.Test.{Guid.NewGuid():N}";
			PipeName = BridgeProtocol.PipeNameFor(2026, Environment.ProcessId, Prefix);
			Dispatcher = new OperationDispatcher(Write);
			Dispatcher.RegisterWithoutMainThread(BridgeOperations.Ping, static _ => Task.FromResult<object?>("pong"));
			register?.Invoke(Dispatcher);
			_server = new BridgeServer(Dispatcher, Write, PipeName);
		}

		public string Prefix { get; }

		public string PipeName { get; }

		public OperationDispatcher Dispatcher { get; }

		public IReadOnlyList<string> Log
		{
			get
			{
				lock (_log)
					return [.. _log];
			}
		}

		public static async Task<Listener> StartAsync(Action<OperationDispatcher>? register = null)
		{
			Listener listener = new(register);
			listener._server.Start();
			ReleaseSelection selection = new(listener.Prefix, string.Empty);
			await TestBridge.WaitUntilAsync(() => selection.PipeExists(listener.PipeName));

			return listener;
		}

		public static McpClientOptions ClientOptions(string protocolVersion) => new()
		{
			ClientInfo = new Implementation { Name = "InventorMcp.Test", Version = "test" },
			ProtocolVersion = protocolVersion
		};

		public void Write(string line)
		{
			lock (_log)
				_log.Add(line);
		}

		// The test process hosts the real listener, so the host check accepts it.
		public BridgeClient Client() =>
			new(NullLogger<BridgeClient>.Instance, new FakeDialogs(), new FakeTimeProvider(), new ReleaseSelection(Prefix, string.Empty), static () => DialogSettings.Defaults, static _ => null);

		/// <summary>
		/// 	An SDK client with no gate, so a test can send several requests on one connection.
		/// </summary>
		public async Task<McpClient> RawClientAsync(string protocolVersion = BridgeProtocol.McpVersion)
		{
			NamedPipeClientStream pipe = new(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
			await pipe.ConnectAsync(5_000, TestContext.Current.CancellationToken);

			using CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
			handshake.CancelAfter(TimeSpan.FromSeconds(10));

			return await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe, NullLoggerFactory.Instance), ClientOptions(protocolVersion), NullLoggerFactory.Instance, handshake.Token);
		}

		public ValueTask DisposeAsync()
		{
			if (_disposed is false)
			{
				_disposed = true;
				_server.Dispose();
			}

			return ValueTask.CompletedTask;
		}
	}
}
