using Microsoft.Extensions.Logging;

namespace InventorMcp.Server.Services;

// The source generator writes these methods, so a log call costs nothing when its level is off.
internal sealed partial class ApiReferenceService
{
	[LoggerMessage(Level = LogLevel.Warning, Message = "No API reference shipped for Inventor {Requested}. Answering from {Fallback} instead.")]
	private partial void LogReferenceFallback(int requested, int fallback);

	[LoggerMessage(Level = LogLevel.Warning, Message = "The Inventor API reference was not found at {Path}. Lookups will return nothing.")]
	private partial void LogReferenceMissing(string path);

	[LoggerMessage(Level = LogLevel.Information, Message = "Indexed {Count} Inventor API members from {Path}.")]
	private partial void LogReferenceIndexed(int count, string path);

	[LoggerMessage(Level = LogLevel.Error, Message = "Could not index the Inventor API reference at {Path}.")]
	private partial void LogReferenceIndexFailed(Exception exception, string path);
}
