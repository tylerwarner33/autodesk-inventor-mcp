using System.Diagnostics.Metrics;
using System.Globalization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace InventorMcp.Server.Telemetry;

/// <summary>
/// 	Registers the OpenTelemetry SDK, which exports the server's traces and metrics over OTLP.
/// </summary>
/// <remarks>
/// 	<c>Program.cs</c> calls it only when <see cref="TelemetrySettings.Enabled"/> is true, so with no setting no
/// 	telemetry package does work.
/// 	See <c>Docs/Architecture.md</c>, "Telemetry is optional, and goes through a Collector".
/// </remarks>
internal static class TelemetryRegistration
{
	/// <summary>
	/// 	The metric export interval when the user sets none.
	/// </summary>
	/// <remarks>
	/// 	A client can end the server with no warning, and the data since the last export is then lost.
	/// 	The SDK's own default is 60 s.
	/// </remarks>
	public static readonly TimeSpan DefaultMetricExportInterval = TimeSpan.FromSeconds(10);

	/// <summary>
	/// 	The time an export can take when the user sets none.
	/// </summary>
	/// <remarks>
	/// 	The last export runs when stdin closes, and the process ends only after it.
	/// 	With the SDK's default of 10 s and no Collector, the server took 6 to 15 s to end.
	/// 	See <c>Docs/Research/OpenTelemetry-Findings.md</c>, "Phase 0 results".
	/// </remarks>
	public static readonly TimeSpan DefaultExportTimeout = TimeSpan.FromMilliseconds(500);

	/// <summary>
	/// 	Adds the SDK, with the server's sources and meter only, the allowlist, and the OTLP exporter.
	/// </summary>
	/// <param name="builder">
	/// 	The host builder.
	/// </param>
	public static void AddServerTelemetry(this IHostApplicationBuilder builder)
	{
		// The SDK reads the OTEL_* variables through the configuration, so a value here is a default that the user's
		// variable replaces.
		SetDefault(builder.Configuration, TelemetrySettings.MetricExportIntervalVariable, DefaultMetricExportInterval);
		SetDefault(builder.Configuration, TelemetrySettings.ExportTimeoutVariable, DefaultExportTimeout);

		// Each source and meter is named, never "*", so a source of another package cannot export a value that the
		// allowlist did not plan for. No host or process resource detector: they send the machine and the user name.
		_ = builder.Services.AddOpenTelemetry()
			.ConfigureResource(static resource => resource.AddService(
				ServerTelemetry.Name,
				serviceVersion: ServerTelemetry.Version,
				autoGenerateServiceInstanceId: true))
			.WithTracing(static tracing => tracing
				.AddSource(ServerTelemetry.Name, ServerTelemetry.LibrarySourceName)
				.AddProcessor(new AllowlistProcessor()))
			// Not the meter of the MCP library: its metrics have the tool and the method name as the client wrote them, and a
			// view cannot change a value. inventor.tool.duration gives the same durations.
			.WithMetrics(static metrics => metrics
				.AddMeter(ServerTelemetry.Name)
				// A measurement inside a span keeps its trace ID, so a point on a duration panel opens its trace. The
				// SDK's default records none.
				.SetExemplarFilter(ExemplarFilterType.TraceBased)
				.AddView(MetricView))
			.UseOtlpExporter();
	}

	private static void SetDefault(IConfigurationManager configuration, string variable, TimeSpan value)
	{
		if (string.IsNullOrWhiteSpace(configuration[variable]))
		{
			_ = configuration.AddInMemoryCollection(
				[new(variable, value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture))]);
		}
	}

	/// <summary>
	/// 	Keeps only the allowed tags of each instrument, and gives each duration the buckets of the MCP conventions.
	/// </summary>
	internal static MetricStreamConfiguration MetricView(Instrument instrument) =>
		instrument is Histogram<double> && instrument.Unit == "s"
			? new ExplicitBucketHistogramConfiguration
			{
				Boundaries = ServerTelemetry.DurationBoundaries,
				TagKeys = TelemetryTags.AllowedOnMetrics
			}
			: new MetricStreamConfiguration { TagKeys = TelemetryTags.AllowedOnMetrics };
}
