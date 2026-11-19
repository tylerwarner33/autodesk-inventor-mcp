using System.Globalization;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

using InventorMcp.Contracts;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace InventorMcp.AddIn.Bridge;

/// <summary>
/// 	Listens on the named pipe, and serves each connection as an MCP session whose methods are the bridge operations.
/// </summary>
/// <remarks>
/// 	The pipe is restricted to the account that started Inventor.
/// 	This matters because the bridge can execute code inside the Inventor process,
/// 	and a loopback socket would be reachable by every process and every logged on user.
/// </remarks>
internal sealed class BridgeServer(OperationDispatcher dispatcher, Action<string> log, string pipeName) : IDisposable
{
	/// <summary>
	/// 	Connections served at once.
	/// </summary>
	/// <remarks>
	/// 	Each MCP client starts its own server, and Claude Desktop starts two, so a normal setup already exceeds four.
	/// 	The pipe ACL admits only the current user, so the limit guards resources, not access.
	/// 	See <c>Docs/Architecture.md</c>, "A named pipe rather than a local HTTP port".
	/// </remarks>
	private const int MaxPipeInstances = 16;

	/// <summary>
	/// 	Pause after a failed accept, so a failure that repeats cannot spin the loop and flood the log.
	/// </summary>
	private static readonly TimeSpan _acceptRetryDelay = TimeSpan.FromSeconds(1);

	/// <summary>
	/// 	One line per accept failure message a minute, with the count of the ones held back.
	/// </summary>
	private readonly LogThrottle _acceptFailureLog = new(TimeProvider.System, TimeSpan.FromMinutes(1));

	/// <summary>
	/// 	The name of the MCP server of the add-in.
	/// </summary>
	private const string ServerName = "InventorMcp.AddIn";

	private readonly OperationDispatcher _dispatcher = dispatcher;
	private readonly Action<string> _log = log;
	private readonly CancellationTokenSource _shutdown = new();

	private Task? _acceptLoop;
	private bool _disposed;

	/// <summary>
	/// 	True after the first instance of the pipe was created, so later instances do not ask for the first.
	/// </summary>
	private bool _claimed;

	/// <summary>
	/// 	The failed claims of the name so far.
	/// </summary>
	private int _claimAttempts;

	/// <summary>
	/// 	The claims of the name before the server stops, one <see cref="_acceptRetryDelay"/> apart.
	/// </summary>
	private const int MaxClaimAttempts = 5;

	/// <summary>
	/// 	The name of the pipe this server listens on.
	/// </summary>
	public string PipeName { get; } = pipeName;

	/// <summary>
	/// 	Starts accepting connections in the background.
	/// </summary>
	public void Start()
	{
		_acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token), _shutdown.Token);
	}

	private async Task AcceptLoopAsync(CancellationToken cancellationToken)
	{
		List<Task> connections = [];

		while (cancellationToken.IsCancellationRequested is false)
		{
			try
			{
				_ = connections.RemoveAll(static connection => connection.IsCompleted);

				// With every instance serving a client, creating another fails at once. Wait for a slot instead.
				if (connections.Count >= MaxPipeInstances)
				{
					_ = await Task.WhenAny(connections).WaitAsync(cancellationToken).ConfigureAwait(false);
					continue;
				}

				NamedPipeServerStream pipe = CreatePipe();

				// Only the first instance claims the name. A later instance joins the name that this process owns.
				_claimed = true;

				await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

				connections.Add(Task.Run(() => ServeConnectionAsync(pipe, cancellationToken), CancellationToken.None));
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (IOException exception)
			{
				// A client that disconnects mid handshake lands here. Keep listening, but never retry at once.
				if (_acceptFailureLog.Filter(exception.Message, $"Pipe accept failed: {exception.Message}") is string line)
					_log(line);

				try
				{
					await Task.Delay(_acceptRetryDelay, cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					return;
				}
			}
			catch (UnauthorizedAccessException exception)
			{
				// The name has the id of this process. After an unload and a load of the add-in in this process, a connection
				// of the old server can still hold the name for a moment, so the claim is tried again a few times.
				// After that, another process holds it and is not an Inventor of this user (a different Windows user, or a
				// program that took the name). Stop rather than spin.
				if (_claimed is false && ++_claimAttempts < MaxClaimAttempts)
				{
					_log($"Could not claim the pipe '{PipeName}' (attempt {_claimAttempts} of {MaxClaimAttempts}). Trying again. {exception.Message}");

					try
					{
						await Task.Delay(_acceptRetryDelay, cancellationToken).ConfigureAwait(false);
					}
					catch (OperationCanceledException)
					{
						return;
					}

					continue;
				}

				_log($"Could not claim the pipe '{PipeName}'. The name has the id of this process, so another process holds it and is not this user's Inventor. {exception.Message}");
				return;
			}
		}
	}

	private NamedPipeServerStream CreatePipe()
	{
		PipeSecurity security = new();

		SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
			?? throw new InvalidOperationException("Could not resolve the current Windows user for the pipe ACL.");

		security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));

		// .NET never sets PIPE_REJECT_REMOTE_CLIENTS. A network logon has the NETWORK SID in its token and an interactive
		// logon has not, so a deny entry blocks a client from another computer. A remote client also needs the
		// credentials of this user, so this is a second barrier and not the first.
		security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));

		return NamedPipeServerStreamAcl.Create(
			PipeName,
			PipeDirection.InOut,
			MaxPipeInstances,
			PipeTransmissionMode.Byte,
			// FirstPipeInstance makes the create fail when the name exists. It protects only the call that has it.
			_claimed ? PipeOptions.Asynchronous : PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
			inBufferSize: 0,
			outBufferSize: 0,
			security);
	}

	private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
	{
		try
		{
			await using (pipe.ConfigureAwait(false))
			{
				// One MCP session for each connection, with its own queue. The SDK reads the requests, and the queue holds
				// them. The pinned version makes the add-in refuse a client of any other MCP version.
				using SemaphoreSlim queue = new(1, 1);

				McpServerOptions options = new()
				{
					ServerInfo = new Implementation { Name = ServerName, Version = BridgeProtocol.Version.ToString(CultureInfo.InvariantCulture) },
					ProtocolVersion = BridgeProtocol.McpVersion,
					RequestHandlers = _dispatcher.CreateRequestHandlers(queue)
				};

				StreamServerTransport transport = new(pipe, pipe, ServerName, NullLoggerFactory.Instance);
				McpServer server = McpServer.Create(transport, options, NullLoggerFactory.Instance);

				await using (server.ConfigureAwait(false))
					await server.RunAsync(cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			// Shutdown.
		}
		catch (IOException)
		{
			// The client went away. Nothing to report.
		}
		catch (Exception exception)
		{
			_log($"Connection failed: {exception}");
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;

		_shutdown.Cancel();

		try
		{
			// Not _shutdown.Token: it is already cancelled, and the loop still gets its time to end.
			_ = _acceptLoop?.Wait(TimeSpan.FromSeconds(2), CancellationToken.None);
		}
		catch (AggregateException)
		{
			// Cancellation surfaces here and is expected.
		}

		_shutdown.Dispose();
	}
}
