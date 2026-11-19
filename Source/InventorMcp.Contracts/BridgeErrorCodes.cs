namespace InventorMcp.Contracts;

/// <summary>
/// 	The stable codes of a bridge failure, which a model can read.
/// </summary>
/// <remarks>
/// 	On the pipe, a failure is a JSON-RPC error with the code <see cref="BridgeResult.ServerErrorCode"/>, and the string
/// 	code is in <c>error.data.code</c>.
/// </remarks>
public static class BridgeErrorCodes
{
	public const string NotRunning = "inventor-not-running";
	public const string NoActiveDocument = "no-active-document";
	public const string WrongDocumentType = "wrong-document-type";
	public const string NotFound = "not-found";
	public const string AmbiguousDocument = "ambiguous-document";
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
	public const string ConnectionsInUse = "bridge-connections-in-use";
	public const string InstanceRequired = "instance-required";
}
