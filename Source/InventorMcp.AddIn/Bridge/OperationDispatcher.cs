using System.Runtime.InteropServices;
using System.Text.Json;

using InventorMcp.Contracts;

namespace InventorMcp.AddIn.Bridge;

/// <summary>
/// 	Maps an operation name to the handler that serves it.
/// </summary>
/// <remarks>
/// 	Handlers are deliberately primitive.
/// 	Shaping and composition belong in the MCP server, because changing this assembly costs an Inventor restart.
/// </remarks>
internal sealed class OperationDispatcher
{
	private const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
	private const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);

	private readonly Dictionary<string, Func<JsonElement?, CancellationToken, Task<object?>>> _handlers = new(StringComparer.Ordinal);
	private readonly Action<string> _log;

	public OperationDispatcher(Action<string> log)
	{
		_log = log;
	}

	/// <summary>
	/// 	Registers the handler for one operation name.
	/// </summary>
	/// <param name="operation">
	/// 	A name from <see cref="BridgeOperations"/>.
	/// </param>
	/// <param name="handler">
	/// 	Receives the raw payload and returns the object to serialize as the result.
	/// </param>
	public void Register(string operation, Func<JsonElement?, CancellationToken, Task<object?>> handler)
	{
		_handlers[operation] = handler;
	}

	/// <summary>
	/// 	Runs the handler for a request and converts any failure into a structured error.
	/// </summary>
	/// <param name="request">
	/// 	The request read off the pipe.
	/// </param>
	/// <param name="cancellationToken">
	/// 	Cancels the wait for the main thread.
	/// </param>
	/// <returns>
	/// 	A response carrying either the result or a <see cref="BridgeError"/>.
	/// </returns>
	public async Task<BridgeResponse> DispatchAsync(BridgeRequest request, CancellationToken cancellationToken)
	{
		if (_handlers.TryGetValue(request.Operation, out Func<JsonElement?, CancellationToken, Task<object?>>? handler) is false)
			return Failure(request, BridgeErrorCodes.UnknownOperation, $"The operation '{request.Operation}' is not supported by this bridge.");

		try
		{
			object? result = await handler(request.Payload, cancellationToken).ConfigureAwait(false);

			JsonElement? serialized = result is null
				? null
				: JsonSerializer.SerializeToElement(result, BridgeProtocol.SerializerOptions);

			return new BridgeResponse(request.Id, true, serialized, null);
		}
		catch (BridgeFailureException failure)
		{
			return Failure(request, failure.Code, failure.Message, failure.Detail);
		}
		catch (COMException comException) when (comException.HResult is RPC_E_CALL_REJECTED or RPC_E_SERVERCALL_RETRYLATER)
		{
			// Inventor is mid command or showing a modal dialog. This is transient, so say so rather than failing hard.
			return Failure(request, BridgeErrorCodes.Busy, "Inventor rejected the call because it is busy. Retry once the current command finishes.", comException.Message);
		}
		catch (COMException comException)
		{
			return Failure(request, BridgeErrorCodes.Internal, "The Inventor API rejected the call.", $"HRESULT 0x{comException.HResult:X8}: {comException.Message}");
		}
		catch (OperationCanceledException)
		{
			return Failure(request, BridgeErrorCodes.Busy, "The request was cancelled before Inventor's main thread could run it.");
		}
		catch (Exception exception)
		{
			_log($"Operation '{request.Operation}' failed: {exception}");

			return Failure(request, BridgeErrorCodes.Internal, exception.Message, exception.ToString());
		}
	}

	private static BridgeResponse Failure(BridgeRequest request, string code, string message, string? detail = null) =>
		new(request.Id, false, null, new BridgeError(code, message, detail));
}

/// <summary>
/// 	Thrown by a handler to return a specific error code instead of a generic internal failure.
/// </summary>
internal sealed class BridgeFailureException : Exception
{
	public BridgeFailureException(string code, string message, string? detail = null) : base(message)
	{
		Code = code;
		Detail = detail;
	}

	/// <summary>
	/// 	A value from <see cref="BridgeErrorCodes"/>.
	/// </summary>
	public string Code { get; }

	/// <summary>
	/// 	Extra context for the caller, or null.
	/// </summary>
	public string? Detail { get; }
}
