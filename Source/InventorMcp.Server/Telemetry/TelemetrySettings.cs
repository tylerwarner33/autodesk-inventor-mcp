namespace InventorMcp.Server.Telemetry;

/// <summary>
/// 	Whether the server exports telemetry, read one time at start.
/// </summary>
/// <remarks>
/// 	Off unless the user sets an OTLP endpoint, so with no setting no data leaves the process.
/// 	The names are the standard OpenTelemetry variables, which the SDK reads too.
/// 	See <c>Docs/Telemetry.md</c>.
/// </remarks>
/// <param name="Enabled">
/// 	True when an endpoint is set and the SDK is not disabled.
/// </param>
/// <param name="Endpoint">
/// 	The OTLP endpoint, or null.
/// </param>
internal sealed record TelemetrySettings(bool Enabled, string? Endpoint)
{
	public const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
	public const string DisabledVariable = "OTEL_SDK_DISABLED";
	public const string MetricExportIntervalVariable = "OTEL_METRIC_EXPORT_INTERVAL";
	public const string ExportTimeoutVariable = "OTEL_EXPORTER_OTLP_TIMEOUT";

	/// <summary>
	/// 	Reads the settings.
	/// </summary>
	/// <param name="variable">
	/// 	Gives the value of an environment variable, or null. A test passes its own, so it does not change the process.
	/// </param>
	/// <returns>
	/// 	The settings.
	/// </returns>
	public static TelemetrySettings Read(Func<string, string?> variable)
	{
		string? endpoint = variable(EndpointVariable);
		bool disabled = string.Equals(variable(DisabledVariable)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

		return string.IsNullOrWhiteSpace(endpoint) || disabled
			? new TelemetrySettings(false, null)
			: new TelemetrySettings(true, endpoint.Trim());
	}
}
