using System.Text.Json;
using System.Text.Json.Serialization;

namespace InventorMcp.Contracts;

/// <summary>
/// 	Constants shared by the MCP server and the Inventor add-in.
/// </summary>
public static class BridgeProtocol
{
	/// <summary>
	/// 	Name of the named pipe the add-in listens on.
	/// </summary>
	/// <remarks>
	/// 	The name is fixed so the server can connect without a discovery step.
	/// 	A second Inventor instance cannot claim the pipe and reports the conflict instead.
	/// </remarks>
	public const string PipeName = "InventorMcp.Bridge";

	/// <summary>
	/// 	Protocol version. The server refuses a bridge that reports a different value.
	/// </summary>
	public const int Version = 1;

	/// <summary>
	/// 	Serializer options used on both sides of the pipe.
	/// </summary>
	/// <remarks>
	/// 	Messages are newline delimited, so the writer must never indent.
	/// 	JSON escapes every control character, so a payload can never contain a raw newline.
	/// </remarks>
	public static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = false
	};
}
