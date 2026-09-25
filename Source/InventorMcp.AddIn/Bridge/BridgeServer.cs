using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

using InventorMcp.Contracts;

namespace InventorMcp.AddIn.Bridge;

/// <summary>
/// 	Listens on the named pipe and hands each request to the operation dispatcher.
/// </summary>
/// <remarks>
/// 	The pipe is restricted to the account that started Inventor.
/// 	This matters because the bridge can execute code inside the Inventor process,
/// 	and a loopback socket would be reachable by every process and every logged on user.
/// </remarks>
internal sealed class BridgeServer : IDisposable
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

	private readonly OperationDispatcher _dispatcher;
	private readonly Action<string> _log;
	private readonly CancellationTokenSource _shutdown = new();

	private Task? _acceptLoop;
	private bool _disposed;

	public BridgeServer(OperationDispatcher dispatcher, Action<string> log)
	{
		_dispatcher = dispatcher;
		_log = log;
	}

	/// <summary>
	/// 	Starts accepting connections in the background.
	/// </summary>
	public void Start()
	{
		_acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
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
				// Another Inventor instance already owns the pipe name. Stop rather than spin.
				_log($"Could not claim the pipe '{BridgeProtocol.PipeName}'. Another Inventor session is probably hosting the bridge. {exception.Message}");
				return;
			}
		}
	}

	private static NamedPipeServerStream CreatePipe()
	{
		PipeSecurity security = new();

		SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
			?? throw new InvalidOperationException("Could not resolve the current Windows user for the pipe ACL.");

		security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));

		return NamedPipeServerStreamAcl.Create(
			BridgeProtocol.PipeName,
			PipeDirection.InOut,
			MaxPipeInstances,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous,
			inBufferSize: 0,
			outBufferSize: 0,
			security);
	}

	private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
	{
		try
		{
			using (pipe)
			{
				// UTF8Encoding without a byte order mark: a preamble would corrupt the first line.
				using StreamReader reader = new(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
				await using StreamWriter writer = new(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

				while (cancellationToken.IsCancellationRequested is false)
				{
					string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

					if (line is null)
						return;

					if (string.IsNullOrWhiteSpace(line))
						continue;

					BridgeResponse response = await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);

					await writer.WriteLineAsync(JsonSerializer.Serialize(response, BridgeProtocol.SerializerOptions)).ConfigureAwait(false);
				}
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

	private async Task<BridgeResponse> HandleLineAsync(string line, CancellationToken cancellationToken)
	{
		BridgeRequest? request;

		try
		{
			request = JsonSerializer.Deserialize<BridgeRequest>(line, BridgeProtocol.SerializerOptions);
		}
		catch (JsonException exception)
		{
			return new BridgeResponse("unknown", false, null, new BridgeError(BridgeErrorCodes.Internal, "The request was not valid JSON.", exception.Message));
		}

		if (request is null)
			return new BridgeResponse("unknown", false, null, new BridgeError(BridgeErrorCodes.Internal, "The request was empty."));

		return await _dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;

		_shutdown.Cancel();

		try
		{
			_ = _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
		}
		catch (AggregateException)
		{
			// Cancellation surfaces here and is expected.
		}

		_shutdown.Dispose();
	}
}
