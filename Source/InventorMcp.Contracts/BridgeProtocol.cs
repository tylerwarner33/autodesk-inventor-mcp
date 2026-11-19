using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InventorMcp.Contracts;

/// <summary>
/// 	Constants shared by the MCP server and the Inventor add-in.
/// </summary>
public static class BridgeProtocol
{
	/// <summary>
	/// 	Name of the pipe that the add-in of one Inventor process listens on, ex. <c>InventorMcp.Bridge.v3.2026.12345</c>.
	/// </summary>
	/// <remarks>
	/// 	The name has the protocol version, the release and the process id, so the server learns the version and the
	/// 	release from the pipe list before it connects, and a second Inventor of the same release has its own pipe.
	/// 	The pipe list is read with no connect, so no message is necessary to find the version.
	/// 	See <c>Docs/Architecture.md</c>, "A named pipe rather than a local HTTP port".
	/// </remarks>
	/// <param name="releaseYear">
	/// 	The release year, ex. 2026.
	/// </param>
	/// <param name="processId">
	/// 	The id of the Inventor process that hosts the add-in.
	/// </param>
	/// <param name="prefix">
	/// 	The start of the name. Only a test passes it.
	/// </param>
	/// <returns>
	/// 	The pipe name.
	/// </returns>
	public static string PipeNameFor(int releaseYear, int processId, string prefix = PipeNamePrefix) =>
		string.Create(CultureInfo.InvariantCulture, $"{prefix}.v{Version}.{releaseYear}.{processId}");

	/// <summary>
	/// 	Says what a pipe name is, and reads the parts of a bridge pipe of protocol version 3 or later.
	/// </summary>
	/// <param name="name">
	/// 	A name from the pipe list.
	/// </param>
	/// <param name="prefix">
	/// 	The start of the name, <see cref="PipeNamePrefix"/> unless a test uses another.
	/// </param>
	/// <param name="protocolVersion">
	/// 	The protocol version, for <see cref="PipeNameKind.Instance"/> and <see cref="PipeNameKind.ReleasePipe"/> (2).
	/// </param>
	/// <param name="releaseYear">
	/// 	The release, for <see cref="PipeNameKind.Instance"/> and <see cref="PipeNameKind.ReleasePipe"/>.
	/// </param>
	/// <param name="processId">
	/// 	The process id, for <see cref="PipeNameKind.Instance"/>.
	/// </param>
	/// <returns>
	/// 	The kind of the name. A name with text in place of a number is <see cref="PipeNameKind.None"/>.
	/// </returns>
	public static PipeNameKind ParsePipeName(string name, string prefix, out int protocolVersion, out int releaseYear, out int processId)
	{
		protocolVersion = 0;
		releaseYear = 0;
		processId = 0;

		if (string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase))
			return PipeNameKind.Legacy;

		if (name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase) is false)
			return PipeNameKind.None;

		string[] parts = name[(prefix.Length + 1)..].Split('.');

		// Protocol 2: <prefix>.<year>
		if (parts.Length == 1 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out releaseYear))
		{
			protocolVersion = 2;

			return PipeNameKind.ReleasePipe;
		}

		// Protocol 3 and later: <prefix>.v<version>.<year>.<process id>
		if (parts.Length == 3
			&& parts[0].Length > 1
			&& parts[0][0] is 'v' or 'V'
			&& int.TryParse(parts[0].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out protocolVersion)
			&& int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out releaseYear)
			&& int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out processId))
		{
			return PipeNameKind.Instance;
		}

		protocolVersion = 0;
		releaseYear = 0;
		processId = 0;

		return PipeNameKind.None;
	}

	/// <summary>
	/// 	The start of every bridge pipe name.
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
	/// 	Protocol version. The pipe name carries it, so the server uses only a bridge of its own version.
	/// </summary>
	public const int Version = 3;

	/// <summary>
	/// 	The MCP protocol version of the bridge. Both sides pin it, and accept no other.
	/// </summary>
	/// <remarks>
	/// 	Without the pin, the version follows the two SDK versions that meet, and an SDK update on one side changes the
	/// 	messages. Change it only together with <see cref="Version"/>, so an add-in of the old version keeps the old pipe
	/// 	name and the server reports it as outdated. See <c>Docs/Architecture.md</c>, "The bridge speaks MCP".
	/// </remarks>
	public const string McpVersion = "2026-07-28";

	/// <summary>
	/// 	Serializer options for the payloads and the results on both sides of the pipe.
	/// </summary>
	/// <remarks>
	/// 	The MCP transport writes one message for each line, so the writer must never indent.
	/// </remarks>
	public static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = false
	};
}

/// <summary>
/// 	What a name in the pipe list is, for <see cref="BridgeProtocol.ParsePipeName"/>.
/// </summary>
public enum PipeNameKind
{
	/// <summary>
	/// 	Not a bridge pipe.
	/// </summary>
	None,

	/// <summary>
	/// 	The pipe of a protocol 1 add-in: the prefix alone.
	/// </summary>
	Legacy,

	/// <summary>
	/// 	The pipe of a protocol 2 add-in, one for each release: <c>&lt;prefix&gt;.&lt;year&gt;</c>.
	/// </summary>
	ReleasePipe,

	/// <summary>
	/// 	The pipe of one process, protocol 3 or later: <c>&lt;prefix&gt;.v&lt;version&gt;.&lt;year&gt;.&lt;process id&gt;</c>.
	/// </summary>
	Instance
}
