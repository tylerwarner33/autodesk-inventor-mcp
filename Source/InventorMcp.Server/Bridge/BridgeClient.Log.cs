using Microsoft.Extensions.Logging;

namespace InventorMcp.Server.Bridge;

// The source generator writes these methods, so a log call costs nothing when its level is off.
internal sealed partial class BridgeClient
{
	[LoggerMessage(Level = LogLevel.Warning, Message = "The bridge connection dropped. Reconnecting once.")]
	private partial void LogConnectionDropped(Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Discarded the late response {ResponseId} from a call that stopped waiting. Waiting for {RequestId}.")]
	private partial void LogLateResponseDiscarded(string responseId, string requestId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Inventor {ProcessId} is blocked, but no dialog of its process was found.")]
	private partial void LogBlockedWithoutDialog(int processId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Dialog settings: {Problem}")]
	private partial void LogDialogSettingsProblem(string problem);

	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "A dialog blocks Inventor {ProcessId}: '{Title}' ({Framework}, {Type}, class {ClassName}). Buttons: {Buttons}. " +
			"Action: {Action}. {ReadError}Text: {Text}")]
	private partial void LogBlockingDialog(
		int processId,
		string title,
		string framework,
		string type,
		string className,
		string buttons,
		string action,
		string readError,
		string text);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the dialog click to the audit log.")]
	private partial void LogAuditWriteFailed(Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Refused the bridge pipe {PipeName}: {Refusal}")]
	private partial void LogPipeRefused(string pipeName, string refusal);

	[LoggerMessage(Level = LogLevel.Information, Message = "Connected to the Inventor bridge on pipe {PipeName}, hosted by process {ProcessId}.")]
	private partial void LogConnected(string pipeName, int? processId);
}
