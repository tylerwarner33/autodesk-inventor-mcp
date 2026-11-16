using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

using InventorMcp.AddIn.Bridge;
using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The real add-in listener against the real client, with no Inventor.
/// </summary>
/// <remarks>
/// 	The test project compiles <c>BridgeServer.cs</c> and <c>OperationDispatcher.cs</c> of the add-in. They use no Inventor
/// 	type, so the two ends of the pipe are tested together, which a fake listener does not do.
/// </remarks>
[Trait("Level", "Unit")]
public sealed class BridgeEndToEndTests
{
	[Fact]
	public async Task TheRealServerAndTheRealClientTalkOnTheNameOfTheProcess()
	{
		(string prefix, string pipeName) = NewName();
		List<string> log = [];
		using BridgeServer server = StartServer(pipeName, log);
		ReleaseSelection selection = new(prefix, string.Empty);
		await WaitForPipeAsync(selection, pipeName);
		await using BridgeClient client = ClientFor(selection);

		string answer = await client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken);

		Assert.Equal("pong", answer);
		Assert.Equal(Environment.ProcessId, client.InventorProcessId);
		Assert.Equal(2026, client.ReleaseYear);
		Assert.Empty(log);
	}

	[Fact]
	public async Task ASecondListenerOnTheSameNameCannotClaimIt()
	{
		(string prefix, string pipeName) = NewName();
		List<string> log = [];
		using BridgeServer first = StartServer(pipeName, log);
		ReleaseSelection selection = new(prefix, string.Empty);
		await WaitForPipeAsync(selection, pipeName);

		// A program that takes the name of this process cannot become a listener of it: FirstPipeInstance refuses.
		using BridgeServer second = StartServer(pipeName, log);
		await TestBridge.WaitUntilAsync(() => log.Any(static line => line.Contains("Could not claim the pipe", StringComparison.Ordinal)));

		await using BridgeClient client = ClientFor(selection);

		Assert.Equal("pong", await client.InvokeAsync<string>(BridgeOperations.Ping, null, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task TheListenerServesManyClientsAtOnce()
	{
		(string prefix, string pipeName) = NewName();
		List<string> log = [];
		using BridgeServer server = StartServer(pipeName, log);
		ReleaseSelection selection = new(prefix, string.Empty);
		await WaitForPipeAsync(selection, pipeName);

		BridgeClient[] clients = [.. Enumerable.Range(0, 4).Select(_ => ClientFor(selection))];

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
		(string prefix, string pipeName) = NewName();
		List<string> log = [];
		using BridgeServer server = StartServer(pipeName, log);
		await WaitForPipeAsync(new ReleaseSelection(prefix, string.Empty), pipeName);

		// A pipe that is connected has the ACL of the server, and a deny entry for the NETWORK SID is part of it.
		await using NamedPipeClientStream client = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		await client.ConnectAsync(5_000, TestContext.Current.CancellationToken);

		PipeSecurity security = client.GetAccessControl();
		SecurityIdentifier network = new(WellKnownSidType.NetworkSid, null);

		Assert.Contains(
			security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>(),
			rule => rule.AccessControlType == AccessControlType.Deny && rule.IdentityReference.Equals(network));
	}

	private static (string Prefix, string PipeName) NewName()
	{
		string prefix = $"InventorMcp.Test.{Guid.NewGuid():N}";

		return (prefix, BridgeProtocol.PipeNameFor(2026, Environment.ProcessId, prefix));
	}

	private static BridgeServer StartServer(string pipeName, List<string> log)
	{
		OperationDispatcher dispatcher = new(line => { lock (log) log.Add(line); });
		dispatcher.Register(BridgeOperations.Ping, static (_, _) => Task.FromResult<object?>("pong"));

		BridgeServer server = new(dispatcher, line => { lock (log) log.Add(line); }, pipeName);
		server.Start();

		return server;
	}

	private static async Task WaitForPipeAsync(ReleaseSelection selection, string pipeName) =>
		await TestBridge.WaitUntilAsync(() => selection.PipeExists(pipeName));

	// The test process hosts the real listener, so the host check accepts it.
	private static BridgeClient ClientFor(ReleaseSelection selection) =>
		new(NullLogger<BridgeClient>.Instance, new FakeDialogs(), new FakeTimeProvider(), selection, static () => DialogSettings.Defaults, static _ => null);
}
