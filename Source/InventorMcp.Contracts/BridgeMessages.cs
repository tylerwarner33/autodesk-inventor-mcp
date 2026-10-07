using System.Text.Json;

namespace InventorMcp.Contracts;

/// <summary>
/// 	A single request sent from the MCP server to the Inventor add-in.
/// </summary>
/// <param name="Id">
/// 	Correlation identifier echoed back on the response.
/// </param>
/// <param name="Operation">
/// 	One of the names in <see cref="BridgeOperations"/>.
/// </param>
/// <param name="Payload">
/// 	Operation specific arguments, or null when the operation takes none.
/// </param>
public sealed record BridgeRequest(string Id, string Operation, JsonElement? Payload);

/// <summary>
/// 	The reply to a <see cref="BridgeRequest"/>.
/// </summary>
/// <param name="Id">
/// 	Correlation identifier copied from the request.
/// </param>
/// <param name="Success">
/// 	True when <paramref name="Result"/> holds the answer, false when <paramref name="Error"/> explains the failure.
/// </param>
/// <param name="Result">
/// 	Operation specific result payload.
/// </param>
/// <param name="Error">
/// 	Failure detail. Populated only when <paramref name="Success"/> is false.
/// </param>
public sealed record BridgeResponse(string Id, bool Success, JsonElement? Result, BridgeError? Error);

/// <summary>
/// 	Structured failure detail.
/// </summary>
/// <param name="Code">
/// 	Stable machine readable code, ex. "no-active-document".
/// </param>
/// <param name="Message">
/// 	Short description suitable for the model to read.
/// </param>
/// <param name="Detail">
/// 	Exception text or COM HRESULT detail, when available.
/// </param>
public sealed record BridgeError(string Code, string Message, string? Detail = null);

/// <summary>
/// 	Well known values for <see cref="BridgeError.Code"/>.
/// </summary>
public static class BridgeErrorCodes
{
	public const string NotRunning = "inventor-not-running";
	public const string NoActiveDocument = "no-active-document";
	public const string WrongDocumentType = "wrong-document-type";
	public const string NotFound = "not-found";
	public const string Busy = "inventor-busy";
	public const string BlockedByDialog = "blocked-by-dialog";
	public const string UnsavedChanges = "unsaved-changes";
	public const string ExecutionFailed = "execution-failed";
	public const string UnknownOperation = "unknown-operation";
	public const string VersionMismatch = "version-mismatch";
	public const string Internal = "internal-error";
	public const string ReleaseRequired = "release-required";
	public const string BridgeOutdated = "bridge-outdated";
	public const string UntrustedHost = "untrusted-bridge-host";
}
