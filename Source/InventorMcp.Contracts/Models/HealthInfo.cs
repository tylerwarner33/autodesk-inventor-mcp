namespace InventorMcp.Contracts.Models;

/// <summary>
/// 	Rebuild and error state of a document.
/// </summary>
/// <param name="Document">
/// 	The document the state was read from.
/// </param>
/// <param name="RequiresUpdate">
/// 	True when the document is out of date.
/// </param>
/// <param name="SickFeatures">
/// 	Features that failed to compute, in browser order.
/// </param>
/// <param name="ErrorCount">
/// 	Number of entries the Inventor error manager is holding.
/// </param>
/// <param name="Errors">
/// 	Entries from the Inventor error manager, newest first.
/// </param>
public sealed record HealthInfo(
	DocumentInfo Document,
	bool RequiresUpdate,
	IReadOnlyList<FeatureHealth> SickFeatures,
	int ErrorCount,
	IReadOnlyList<ErrorEntry> Errors);

/// <summary>
/// 	Health of one feature.
/// </summary>
/// <param name="Name">
/// 	Feature name as shown in the browser.
/// </param>
/// <param name="FeatureType">
/// 	Inventor feature type name, ex. "ExtrudeFeature".
/// </param>
/// <param name="HealthStatus">
/// 	Inventor health status, ex. "UpToDateHealth", "UnknownHealth", or "ComputeErrorHealth".
/// </param>
/// <param name="IsSuppressed">
/// 	True when the feature is suppressed.
/// </param>
/// <param name="Message">
/// 	Failure text Inventor recorded for the feature, or an empty string.
/// </param>
public sealed record FeatureHealth(
	string Name,
	string FeatureType,
	string HealthStatus,
	bool IsSuppressed,
	string Message);

/// <summary>
/// 	One entry from the Inventor error manager.
/// </summary>
/// <param name="Severity">
/// 	Reported severity, ex. "Error" or "Warning".
/// </param>
/// <param name="Message">
/// 	Message text.
/// </param>
/// <param name="ObjectName">
/// 	Name of the object the entry refers to, or an empty string.
/// </param>
public sealed record ErrorEntry(
	string Severity,
	string Message,
	string ObjectName);
