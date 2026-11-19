using System.Text.Json;
using System.Text.Json.Nodes;

namespace InventorMcp.Contracts;

/// <summary>
/// 	The shape of a bridge result and of a bridge error on the pipe.
/// </summary>
/// <remarks>
/// 	An MCP result must be an object, and an operation can return a string or nothing. So each result is an object with
/// 	the value in <see cref="ValueMember"/>, and no member for no value.
/// 	The wrapper also keeps a null result away from the SDK: SDK 2.4.0 writes a null result with no <c>result</c>
/// 	member, and its client then never completes the call. See <c>Docs/Research/Mcp-Over-Pipe-Spike.md</c>, "SDK
/// 	behavior to check again on each SDK update".
/// </remarks>
public static class BridgeResult
{
	/// <summary>
	/// 	The member of the result object that holds the value.
	/// </summary>
	public const string ValueMember = "value";

	/// <summary>
	/// 	The JSON-RPC error code of a failure that has a <see cref="BridgeErrorCodes"/> value.
	/// </summary>
	/// <remarks>
	/// 	The first code of the range that JSON-RPC 2.0 keeps for the errors of an implementation.
	/// </remarks>
	public const int ServerErrorCode = -32000;

	/// <summary>
	/// 	The member of <c>error.data</c> that holds the <see cref="BridgeErrorCodes"/> value.
	/// </summary>
	public const string CodeMember = "code";

	/// <summary>
	/// 	The member of <c>error.data</c> that holds the detail of a failure.
	/// </summary>
	public const string DetailMember = "detail";

	/// <summary>
	/// 	The member of <c>error.data</c> that holds the message of a failure, as the add-in wrote it.
	/// </summary>
	/// <remarks>
	/// 	The SDK client puts "Request failed (remote): " before <c>error.message</c>, so the message for the model comes
	/// 	from here.
	/// </remarks>
	public const string MessageMember = "message";

	/// <summary>
	/// 	Puts a value into a result object.
	/// </summary>
	/// <param name="value">
	/// 	The value of the operation, or null.
	/// </param>
	/// <returns>
	/// 	The result object.
	/// </returns>
	public static JsonObject Wrap(object? value) =>
		value is null
			? []
			: new JsonObject { [ValueMember] = JsonSerializer.SerializeToNode(value, BridgeProtocol.SerializerOptions) };

	/// <summary>
	/// 	Reads the value from a result object.
	/// </summary>
	/// <typeparam name="TResult">
	/// 	The type of the value.
	/// </typeparam>
	/// <param name="result">
	/// 	The result of the response.
	/// </param>
	/// <returns>
	/// 	The value, or the default of <typeparamref name="TResult"/> when the result has none.
	/// </returns>
	public static TResult? Unwrap<TResult>(JsonNode? result) =>
		result is JsonObject resultObject && resultObject.TryGetPropertyValue(ValueMember, out JsonNode? value) && value is not null
			? value.Deserialize<TResult>(BridgeProtocol.SerializerOptions)
			: default;
}
