using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using InventorMcp.Contracts;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace InventorMcp.AddIn.Bridge;

/// <summary>
/// 	Maps an operation name to the handler that serves it, and makes the MCP request handlers of the pipe.
/// </summary>
/// <remarks>
/// 	Handlers are deliberately primitive.
/// 	Shaping and composition belong in the MCP server, because changing this assembly costs an Inventor restart.
/// </remarks>
internal sealed class OperationDispatcher(Action<string> log)
{
	private const int CallRejected = unchecked((int)0x80010001);
	private const int ServerCallRetryLater = unchecked((int)0x8001010A);

	/// <summary>
	/// 	The member of the params that the MCP client adds to each request on <see cref="BridgeProtocol.McpVersion"/>.
	/// </summary>
	private const string MetaMember = "_meta";

	private readonly Dictionary<string, Func<JsonElement?, CancellationToken, Task<object?>>> _handlers = new(StringComparer.Ordinal);
	private readonly Action<string> _log = log;

	/// <summary>
	/// 	Registers the handler for one operation that uses Inventor's main thread.
	/// </summary>
	/// <param name="operation">
	/// 	A name from <see cref="BridgeOperations"/>.
	/// </param>
	/// <param name="handler">
	/// 	Receives the raw payload and returns the object to serialize as the result.
	/// </param>
	/// <exception cref="ArgumentException">
	/// 	<see cref="BridgeOperations.RunsWithoutMainThread"/> is true for the operation.
	/// </exception>
	public void Register(string operation, Func<JsonElement?, CancellationToken, Task<object?>> handler)
	{
		if (BridgeOperations.RunsWithoutMainThread(operation))
			throw new ArgumentException($"'{operation}' runs without the main thread. Register it with {nameof(RegisterWithoutMainThread)}.", nameof(operation));

		_handlers[operation] = handler;
	}

	/// <summary>
	/// 	Registers the handler for one operation that never uses Inventor's main thread.
	/// </summary>
	/// <remarks>
	/// 	The request skips the queue of its connection, so the handler must never call the main thread dispatcher.
	/// </remarks>
	/// <param name="operation">
	/// 	A name for which <see cref="BridgeOperations.RunsWithoutMainThread"/> is true.
	/// </param>
	/// <param name="handler">
	/// 	Receives the raw payload and returns the object to serialize as the result.
	/// </param>
	/// <exception cref="ArgumentException">
	/// 	<see cref="BridgeOperations.RunsWithoutMainThread"/> is false for the operation.
	/// </exception>
	public void RegisterWithoutMainThread(string operation, Func<JsonElement?, Task<object?>> handler)
	{
		if (BridgeOperations.RunsWithoutMainThread(operation) is false)
			throw new ArgumentException($"'{operation}' uses the main thread. Register it with {nameof(Register)}, or add it to {nameof(BridgeOperations)}.{nameof(BridgeOperations.RunsWithoutMainThread)}.", nameof(operation));

		_handlers[operation] = (payload, _) => handler(payload);
	}

	/// <summary>
	/// 	Makes the MCP request handlers of one connection, one for each registered operation.
	/// </summary>
	/// <param name="queue">
	/// 	The queue of the connection, a semaphore with one slot. The requests of one server then run one at a time and
	/// 	in order.
	/// </param>
	/// <remarks>
	/// 	The SDK runs the requests of one connection at the same time. Without the queue, a call that the server canceled
	/// 	and the next call could run together, and the next call could run nested in the modal loop of a dialog that the
	/// 	first call opened.
	/// </remarks>
	/// <returns>
	/// 	The handlers for <see cref="McpServerOptions.RequestHandlers"/>.
	/// </returns>
	public McpServerRequestHandler[] CreateRequestHandlers(SemaphoreSlim queue) =>
	[
		.. _handlers.Keys.Select(operation => new McpServerRequestHandler
		{
			Method = operation,
			Handler = async (context, cancellationToken) => await HandleAsync(operation, queue, context.Params, cancellationToken).ConfigureAwait(false)
		})
	];

	/// <summary>
	/// 	Runs one request in the queue of its connection, unless the operation never uses the main thread.
	/// </summary>
	/// <param name="operation">
	/// 	The method of the request.
	/// </param>
	/// <param name="queue">
	/// 	The queue of the connection.
	/// </param>
	/// <param name="parameters">
	/// 	The params of the request.
	/// </param>
	/// <param name="cancellationToken">
	/// 	Canceled when the server cancels the request or the connection ends.
	/// </param>
	/// <returns>
	/// 	The result object.
	/// </returns>
	private async Task<JsonNode?> HandleAsync(string operation, SemaphoreSlim queue, JsonNode? parameters, CancellationToken cancellationToken)
	{
		if (BridgeOperations.RunsWithoutMainThread(operation))
			return await DispatchAsync(operation, parameters, cancellationToken).ConfigureAwait(false);

		try
		{
			await queue.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw Logged(operation, Failure(BridgeErrorCodes.Busy, "The request was canceled before it started."));
		}

		try
		{
			return await DispatchAsync(operation, parameters, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_ = queue.Release();
		}
	}

	/// <summary>
	/// 	Runs the handler of an operation, and converts any failure into an MCP error with a bridge code.
	/// </summary>
	/// <param name="operation">
	/// 	The method of the request.
	/// </param>
	/// <param name="parameters">
	/// 	The params of the request.
	/// </param>
	/// <param name="cancellationToken">
	/// 	Cancels the wait for the main thread.
	/// </param>
	/// <returns>
	/// 	The result object, never null.
	/// </returns>
	/// <exception cref="McpProtocolException">
	/// 	The operation failed. <see cref="Exception.Data"/> has the <see cref="BridgeErrorCodes"/> value.
	/// </exception>
	internal async Task<JsonNode?> DispatchAsync(string operation, JsonNode? parameters, CancellationToken cancellationToken)
	{
		if (_handlers.TryGetValue(operation, out Func<JsonElement?, CancellationToken, Task<object?>>? handler) is false)
			throw new McpProtocolException($"The operation '{operation}' is not supported by this bridge.", McpErrorCode.MethodNotFound);

		try
		{
			object? result = await handler(ToPayload(parameters), cancellationToken).ConfigureAwait(false);

			return BridgeResult.Wrap(result);
		}
		// Each failure is written to addin.log, so the log shows what failed and not only what started.
		catch (BridgeFailureException failure)
		{
			throw Logged(operation, Failure(failure.Code, failure.Message, failure.Detail));
		}
		catch (COMException comException) when (comException.HResult is CallRejected or ServerCallRetryLater)
		{
			// Inventor is mid command or showing a modal dialog. This is transient, so say so rather than failing hard.
			throw Logged(operation, Failure(BridgeErrorCodes.Busy, "Inventor rejected the call because it is busy. Retry once the current command finishes.", comException.Message));
		}
		catch (COMException comException)
		{
			throw Logged(operation, Failure(BridgeErrorCodes.Internal, "The Inventor API rejected the call.", $"HRESULT 0x{comException.HResult:X8}: {comException.Message}"));
		}
		catch (OperationCanceledException)
		{
			throw Logged(operation, Failure(BridgeErrorCodes.Busy, "The request was canceled before Inventor's main thread could run it."));
		}
		catch (Exception exception)
		{
			_log($"Operation '{operation}' failed: {exception}");

			throw Failure(BridgeErrorCodes.Internal, exception.Message, exception.ToString());
		}
	}

	/// <summary>
	/// 	Gives the payload of a handler: the params without the member that the MCP client adds.
	/// </summary>
	private static JsonElement? ToPayload(JsonNode? parameters)
	{
		if (parameters is JsonObject parameterObject && parameterObject.ContainsKey(MetaMember))
		{
			JsonObject payload = parameterObject.DeepClone().AsObject();
			_ = payload.Remove(MetaMember);

			parameters = payload.Count == 0 ? null : payload;
		}

		return parameters is null ? null : JsonSerializer.SerializeToElement(parameters, BridgeProtocol.SerializerOptions);
	}

	private static McpProtocolException Failure(string code, string message, string? detail = null)
	{
		McpProtocolException exception = new(message, (McpErrorCode)BridgeResult.ServerErrorCode);
		exception.Data[BridgeResult.CodeMember] = code;
		exception.Data[BridgeResult.MessageMember] = message;

		if (detail is not null)
			exception.Data[BridgeResult.DetailMember] = detail;

		return exception;
	}

	private McpProtocolException Logged(string operation, McpProtocolException failure)
	{
		object? detail = failure.Data[BridgeResult.DetailMember];
		_log($"Operation '{operation}' failed with {failure.Data[BridgeResult.CodeMember]}: {failure.Message}{(detail is null ? string.Empty : $" ({detail})")}");

		return failure;
	}
}

/// <summary>
/// 	Thrown by a handler to return a specific error code instead of a generic internal failure.
/// </summary>
internal sealed class BridgeFailureException(string code, string message, string? detail = null) : Exception(message)
{
	/// <summary>
	/// 	A value from <see cref="BridgeErrorCodes"/>.
	/// </summary>
	public string Code { get; } = code;

	/// <summary>
	/// 	Extra context for the caller, or null.
	/// </summary>
	public string? Detail { get; } = detail;
}
