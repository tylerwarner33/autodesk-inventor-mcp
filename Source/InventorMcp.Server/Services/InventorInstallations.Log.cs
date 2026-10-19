using Microsoft.Extensions.Logging;

namespace InventorMcp.Server.Services;

// The source generator writes these methods, so a log call costs nothing when its level is off.
internal sealed partial class InventorInstallations
{
	[LoggerMessage(Level = LogLevel.Information, Message = "Ignoring {ProductName}, which does not load add-ins.")]
	private partial void LogLiteReleaseIgnored(string productName);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Inventor {Year} is registered at {InstallLocation}, but {ExecutablePath} does not exist.")]
	private partial void LogExecutableMissing(int year, string installLocation, string executablePath);
}
