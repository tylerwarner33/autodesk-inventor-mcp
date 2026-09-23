using System.IO.Pipes;
using System.Text;
using System.Text.Json;

using InventorMcp.Contracts;
using InventorMcp.Server.Services;

using Microsoft.Extensions.Logging;

namespace InventorMcp.Server.Bridge;

/// <summary>
/// 	Talks to the Inventor add-in over the named pipe.
/// </summary>
/// <remarks>
/// 	Requests are serialised one at a time.
/// 	Everything they reach lands on Inventor's single main thread anyway, so overlapping them would only queue deeper,
/// 	and a single write then read pair cannot desynchronise the stream.
/// </remarks>
internal sealed class BridgeClient(ILogger<BridgeClient> logger) : IAsyncDisposable
{
	private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

	private readonly ILogger<BridgeClient> _logger = logger;
	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <summary>
	/// 	Inventor release last seen on the pipe, ex. 2025, or null until a session call has succeeded.
	/// </summary>
	/// <remarks>
	/// 	Lets version specific data, such as the API documentation, follow whichever session answered.
	/// </remarks>
	public int? ReleaseYear { get; private set; }

	private NamedPipeClientStream? _pipe;
	private StreamReader? _reader;
	private StreamWriter? _writer;

	/// <summary>
	/// 	Sends one operation and returns its typed result.
	/// </summary>
	/// <typeparam name="TResult">
	/// 	Type the operation returns.
	/// </typeparam>
	/// <param name="operation">
	/// 	A name from <see cref="BridgeOperations"/>.
	/// </param>
	/// <param name="payload">
	/// 	Operation arguments, or null.
	/// </param>
	/// <param name="cancellationToken">
	/// 	Cancels the call.
	/// </param>
	/// <returns>
	/// 	The deserialised result.
	/// </returns>
	public async Task<TResult> InvokeAsync<TResult>(string operation, object? payload, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			// A dropped pipe surfaces only on use, so one reconnect and retry is expected rather than exceptional.
			TResult result;

			try
			{
				result = await SendAsync<TResult>(operation, payload, cancellationToken).ConfigureAwait(false);
			}
			catch (IOException exception)
			{
				_logger.LogWarning(exception, "The bridge connection dropped. Reconnecting once.");

				await CloseAsync().ConfigureAwait(false);

				result = await SendAsync<TResult>(operation, payload, cancellationToken).ConfigureAwait(false);
			}

			if (result is Contracts.Models.SessionInfo session)
				ReleaseYear = session.ReleaseYear;

			return result;
		}
		finally
		{
			_ = _gate.Release();
		}
	}

	/// <summary>
	/// 	Connects to the pipe without sending a request.
	/// </summary>
	/// <remarks>
	/// 	A connection proves the add-in is hosting the bridge.
	/// 	A request could block on a dialog that holds Inventor's main thread at startup, so none is sent.
	/// </remarks>
	/// <param name="cancellationToken">
	/// 	Cancels the attempt.
	/// </param>
	/// <returns>
	/// 	True when the pipe is connected.
	/// </returns>
	public async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

			return true;
		}
		catch (InventorBridgeException exception) when (exception.Code == BridgeErrorCodes.NotRunning)
		{
			return false;
		}
		finally
		{
			_ = _gate.Release();
		}
	}

	private async Task<TResult> SendAsync<TResult>(string operation, object? payload, CancellationToken cancellationToken)
	{
		await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

		JsonElement? serialisedPayload = payload is null
			? null
			: JsonSerializer.SerializeToElement(payload, BridgeProtocol.SerializerOptions);

		BridgeRequest request = new(Guid.NewGuid().ToString("N"), operation, serialisedPayload);

		await _writer!.WriteLineAsync(JsonSerializer.Serialize(request, BridgeProtocol.SerializerOptions)).ConfigureAwait(false);

		string? line = await _reader!.ReadLineAsync(cancellationToken).ConfigureAwait(false)
			?? throw new IOException("The Inventor bridge closed the connection.");

		BridgeResponse response = JsonSerializer.Deserialize<BridgeResponse>(line, BridgeProtocol.SerializerOptions)
			?? throw new InventorBridgeException(BridgeErrorCodes.Internal, "The bridge returned an empty response.");

		if (response.Success is false)
		{
			BridgeError error = response.Error ?? new BridgeError(BridgeErrorCodes.Internal, "The bridge reported a failure with no detail.");

			throw new InventorBridgeException(error.Code, error.Message, error.Detail);
		}

		if (response.Result is not JsonElement result || result.ValueKind is JsonValueKind.Null)
			return default!;

		return JsonSerializer.Deserialize<TResult>(result, BridgeProtocol.SerializerOptions)!;
	}

	private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
	{
		if (_pipe is { IsConnected: true })
			return;

		await CloseAsync().ConfigureAwait(false);

		NamedPipeClientStream pipe = new(".", BridgeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

		try
		{
			await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			await pipe.DisposeAsync().ConfigureAwait(false);

			// The code stays NotRunning even with Inventor open, because inventor_start polls on it while Inventor loads.
			// Only the message tells the model not to start a second Inventor.
			throw new InventorBridgeException(
				BridgeErrorCodes.NotRunning,
				InventorInstallations.FindRunning().Count > 0
					? "Inventor is running, but its MCP bridge accepted no connection within " +
						$"{ConnectTimeout.TotalSeconds:0} s. The add-in may still be loading or may not be loaded, or every bridge " +
						"connection may be in use by other MCP clients. Do not call inventor_start. Retry shortly, and if it " +
						"persists, ask the user to check Tools > Add-Ins in Inventor or to close another MCP client."
					: "No Inventor session is hosting the MCP bridge. Ask the user whether to start Inventor, then call " +
						"inventor_start. If Inventor is already open, make sure the Inventor MCP Bridge add-in is loaded.");
		}

		_pipe = pipe;
		_reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
		_writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

		_logger.LogInformation("Connected to the Inventor bridge on pipe {PipeName}.", BridgeProtocol.PipeName);
	}

	private async Task CloseAsync()
	{
		// Disposing the writer flushes, which throws on a pipe whose Inventor has closed.
		// Closing must still succeed, or the reconnect after it never runs.
		try
		{
			if (_writer is not null)
				await _writer.DisposeAsync().ConfigureAwait(false);
		}
		catch (IOException)
		{
		}

		_reader?.Dispose();

		try
		{
			if (_pipe is not null)
				await _pipe.DisposeAsync().ConfigureAwait(false);
		}
		catch (IOException)
		{
		}

		_writer = null;
		_reader = null;
		_pipe = null;

		// The next connection may be a different Inventor release.
		ReleaseYear = null;
	}

	public async ValueTask DisposeAsync()
	{
		await CloseAsync().ConfigureAwait(false);
		_gate.Dispose();
	}
}

/// <summary>
/// 	A failure the Inventor bridge reported, or a failure reaching it.
/// </summary>
internal sealed class InventorBridgeException(string code, string message, string? detail = null) : Exception(message)
{
	/// <summary>
	/// 	A value from <see cref="BridgeErrorCodes"/>.
	/// </summary>
	public string Code { get; } = code;

	/// <summary>
	/// 	Extra context, or null.
	/// </summary>
	public string? Detail { get; } = detail;
}
