using Microsoft.Extensions.Logging;

namespace InventorMcp.Server.McpTools;

// The source generator writes these methods, so a log call costs nothing when its level is off.
internal static partial class InventorTool
{
	[LoggerMessage(Level = LogLevel.Information, Message = "Tool {Tool} result: the code failed with {ErrorType} after {WallClockMilliseconds} ms.")]
	private static partial void LogCodeFailed(ILogger logger, string tool, string errorType, long wallClockMilliseconds);

	[LoggerMessage(Level = LogLevel.Information, Message = "Tool {Tool} result: error {ErrorCode} after {WallClockMilliseconds} ms.")]
	private static partial void LogBridgeError(ILogger logger, string tool, string errorCode, long wallClockMilliseconds);
}
