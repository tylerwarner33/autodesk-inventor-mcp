using System.Collections.Frozen;
using System.Reflection;

using InventorMcp.Contracts;

using ModelContextProtocol.Protocol;

namespace InventorMcp.Server.Telemetry;

/// <summary>
/// 	The tag names and values that the server exports, and the allowlist that removes every other tag.
/// </summary>
/// <remarks>
/// 	Every tag that the server sets is a constant here, and a unit test fails on a tag name written as a literal.
/// 	A new tag must go in <see cref="Allowed"/>, and in the allowlist of the Collector in <c>Tools/Telemetry</c>.
/// 	See <c>Docs/Telemetry.md</c>, "What is sent".
/// </remarks>
internal static class TelemetryTags
{
	public const string ToolName = "gen_ai.tool.name";
	public const string Outcome = "inventor.outcome";
	public const string ErrorType = "error.type";
	public const string Release = "inventor.release";
	public const string ReleaseSelection = "inventor.release.selection";
	public const string ClientName = "inventor.client.name";
	public const string BridgeOperation = "inventor.bridge.operation";
	public const string SkillName = "inventor.skill.name";
	public const string DialogType = "inventor.dialog.type";
	public const string DialogAction = "inventor.dialog.action";
	public const string HealthStatus = "inventor.health.status";

	// The tags of the MCP library that the allowlist keeps. The library sets them, not the server.
	public const string OperationName = "gen_ai.operation.name";
	public const string MethodName = "mcp.method.name";
	public const string ProtocolVersion = "mcp.protocol.version";
	public const string SessionId = "mcp.session.id";
	public const string ResponseStatusCode = "rpc.response.status_code";
	public const string NetworkTransport = "network.transport";
	public const string JsonRpcProtocolVersion = "jsonrpc.protocol.version";
	public const string RequestId = "jsonrpc.request.id";

	public const string OutcomeOk = "ok";
	public const string OutcomeBridgeError = "bridge-error";
	public const string OutcomeCodeFailed = "code-failed";
	public const string OutcomeException = "exception";

	/// <summary>
	/// 	A tool that returned its own <c>error</c> with no bridge call, ex. <c>invalid-arguments</c>.
	/// </summary>
	public const string OutcomeToolError = "tool-error";

	public const string DialogClicked = "clicked";
	public const string DialogClosedElsewhere = "closed-elsewhere";
	public const string DialogClickFailed = "click-failed";
	public const string DialogNeedsPerson = "needs-person";

	/// <summary>
	/// 	The value of <see cref="Release"/> with no connection, and of <see cref="DialogType"/> for a type that is not in
	/// 	the catalog.
	/// </summary>
	public const string None = "none";

	/// <summary>
	/// 	The value of <see cref="ErrorType"/> for a name that the server does not send.
	/// </summary>
	public const string Other = "other";

	/// <summary>
	/// 	The value of <see cref="ErrorType"/> for a snippet that did not compile.
	/// </summary>
	public const string CompilationError = "compilation-error";

	/// <summary>
	/// 	The value of <see cref="ErrorType"/> that the MCP library and the semantic conventions use for an <c>isError</c>
	/// 	result.
	/// </summary>
	public const string ToolError = "tool_error";

	/// <summary>
	/// 	The longest <see cref="ClientName"/>.
	/// </summary>
	public const int MaxClientNameLength = 64;

	/// <summary>
	/// 	Every span tag that is exported. The server removes all other tags before the export.
	/// </summary>
	public static FrozenSet<string> Allowed { get; } = FrozenSet.Create(
		StringComparer.Ordinal,
		ToolName, Outcome, ErrorType, Release, ReleaseSelection, ClientName, BridgeOperation, SkillName, DialogType,
		DialogAction, HealthStatus, OperationName, MethodName, ProtocolVersion, SessionId, ResponseStatusCode,
		NetworkTransport, JsonRpcProtocolVersion, RequestId);

	/// <summary>
	/// 	Every metric tag that is exported. The same as <see cref="Allowed"/>, without the IDs of one session or one
	/// 	request, which would make a new time series for each value.
	/// </summary>
	public static string[] AllowedOnMetrics { get; } =
		[.. Allowed.Where(static key => key is not SessionId and not RequestId).Order(StringComparer.Ordinal)];

	private static readonly string[] _exceptionNamespaces = ["System.", "Microsoft.", "Inventor.", "Autodesk."];

	private static readonly FrozenSet<string> _bridgeErrorCodes = ConstantValues(typeof(BridgeErrorCodes));

	private static readonly FrozenSet<string> _bridgeOperations = ConstantValues(typeof(BridgeOperations));

	private static readonly FrozenSet<string> _methodNames =
		ConstantValues(typeof(RequestMethods)).Union(ConstantValues(typeof(NotificationMethods)), StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal);

	/// <summary>
	/// 	The <c>error</c> codes that a tool writes itself, in addition to <see cref="BridgeErrorCodes"/>.
	/// </summary>
	/// <remarks>
	/// 	<c>TelemetryTagsTests</c> fails on a code in <c>McpTools</c> that is not here.
	/// </remarks>
	internal static FrozenSet<string> ToolErrorCodes { get; } = FrozenSet.Create(
		StringComparer.Ordinal,
		"addin-not-deployed", "anchor-not-unique", "copy-failed", "invalid-arguments", "invalid-handle", "inventor-exited",
		"inventor-not-installed", "inventor-starting-or-no-bridge", "rule-changed", "start-failed", "target-exists",
		"too-slow", "version-not-installed", "version-not-offered", "version-not-supported", "version-required");

	/// <summary>
	/// 	The health statuses of a feature, as the add-in names them in <c>InventorOperations.Health.cs</c>.
	/// </summary>
	private static readonly FrozenSet<string> _healthStatuses = FrozenSet.Create(
		StringComparer.Ordinal,
		"BeyondStopNode", "CannotCompute", "Deleted", "DriverLost", "InError", "Inconsistent", "InvalidLimits",
		"JointDegreesOfFreedomLocked", "NewlyAdded", "OutOfDate", "Redundant", "Suppressed", "Unknown", "UpToDate");

	/// <summary>
	/// 	The value of <see cref="HealthStatus"/> for a status from the add-in.
	/// </summary>
	/// <remarks>
	/// 	The add-in sends the enum name for a status it does not know, which a later Inventor can add.
	/// </remarks>
	public static string HealthStatusValue(string status) => _healthStatuses.Contains(status) ? status : Other;

	/// <summary>
	/// 	The value of <see cref="Release"/> for a release year.
	/// </summary>
	public static string ReleaseValue(int? releaseYear) =>
		releaseYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? None;

	/// <summary>
	/// 	The value of <see cref="ErrorType"/> for an exception type name.
	/// </summary>
	/// <remarks>
	/// 	A snippet or a plugin can declare its own exception class, and its name can hold client data.
	/// 	So only a type of .NET or of Inventor keeps its name.
	/// </remarks>
	/// <param name="typeName">
	/// 	The full name of the type, or null.
	/// </param>
	/// <returns>
	/// 	The name, <see cref="CompilationError"/>, or <see cref="Other"/>.
	/// </returns>
	public static string ExceptionTypeValue(string? typeName)
	{
		if (typeName is "CompilationError" or "CompilationErrorException" or "Microsoft.CodeAnalysis.Scripting.CompilationErrorException")
			return CompilationError;

		// A generic argument can name a type of the snippet, ex. FaultException`1[[Client.Detail, ...]].
		if (typeName is null || typeName.IndexOfAny(['`', '[', ',', ' ', '+', '#']) >= 0)
			return Other;

		return _exceptionNamespaces.Any(prefix => typeName.StartsWith(prefix, StringComparison.Ordinal)) ? typeName : Other;
	}

	/// <summary>
	/// 	The value of <see cref="ErrorType"/> for a bridge error code, which comes from the add-in.
	/// </summary>
	public static string BridgeErrorValue(string code) => _bridgeErrorCodes.Contains(code) ? code : Other;

	/// <summary>
	/// 	The value of <see cref="ErrorType"/> for the <c>error</c> code of a tool result.
	/// </summary>
	public static string ToolErrorValue(string code) =>
		_bridgeErrorCodes.Contains(code) || ToolErrorCodes.Contains(code) ? code : Other;

	/// <summary>
	/// 	The value of <see cref="BridgeOperation"/> for an operation name.
	/// </summary>
	public static string BridgeOperationValue(string operation) => _bridgeOperations.Contains(operation) ? operation : Other;

	/// <summary>
	/// 	The value of <see cref="MethodName"/> for the method of a request, which the client writes.
	/// </summary>
	/// <remarks>
	/// 	The MCP library sends an unknown method name back in its span name and in this tag.
	/// </remarks>
	public static string MethodNameValue(string? method) => method is not null && _methodNames.Contains(method) ? method : Other;

	/// <summary>
	/// 	The value of <see cref="ClientName"/>, cut to <see cref="MaxClientNameLength"/>.
	/// </summary>
	public static string ClientNameValue(string? clientName) =>
		clientName is null ? None
		: clientName.Length > MaxClientNameLength ? clientName[..MaxClientNameLength]
		: clientName;

	private static FrozenSet<string> ConstantValues(Type type) =>
		type.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(static field => field.IsLiteral && field.FieldType == typeof(string))
			.Select(static field => (string)field.GetRawConstantValue()!)
			.ToFrozenSet(StringComparer.Ordinal);
}
