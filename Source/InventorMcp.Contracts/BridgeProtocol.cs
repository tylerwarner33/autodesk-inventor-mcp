using System.Text.Json;
using System.Text.Json.Serialization;

namespace InventorMcp.Contracts;

/// <summary>
/// 	Constants shared by the MCP server and the Inventor add-in.
/// </summary>
public static class BridgeProtocol
{
	/// <summary>
	/// 	Name of the pipe the add-in of one Inventor release listens on, ex. <c>InventorMcp.Bridge.2026</c>.
	/// </summary>
	/// <remarks>
	/// 	The name is fixed for a release, so the server can connect without a discovery step.
	/// 	A second Inventor of the same release cannot claim the pipe and reports the conflict instead.
	/// </remarks>
	/// <param name="releaseYear">
	/// 	The release year, ex. 2026.
	/// </param>
	/// <returns>
	/// 	The pipe name.
	/// </returns>
	public static string PipeNameFor(int releaseYear) => $"{PipeNamePrefix}.{releaseYear}";

	/// <summary>
	/// 	The start of every release pipe name, before the year.
	/// </summary>
	public const string PipeNamePrefix = "InventorMcp.Bridge";

	/// <summary>
	/// 	The pipe name of a protocol 1 add-in, which listened on one name for every release.
	/// </summary>
	/// <remarks>
	/// 	Used only to tell the user that the add-in is outdated. It is not a release name.
	/// </remarks>
	public const string LegacyPipeName = PipeNamePrefix;

	/// <summary>
	/// 	Protocol version. The server refuses a bridge that reports a different value.
	/// </summary>
	public const int Version = 2;

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
