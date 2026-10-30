using System.Diagnostics;

using InventorMcp.Server.Telemetry;

using OpenTelemetry.Metrics;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The server's guard: only the allowed tags leave the process.
/// </summary>
[Trait("Level", "Unit")]
public sealed class AllowlistProcessorTests
{
	[Fact]
	public void SpanKeepsOnlyTheAllowedTagsAndLosesItsStatusText()
	{
		using TelemetryCapture capture = new();
		using Activity span = TelemetryCapture.TestSource.StartActivity("tools/call test")!;

		_ = span
			.SetTag(TelemetryTags.ToolName, "inventor_session")
			.SetTag(TelemetryTags.RequestId, "7")
			.SetTag("mcp.resource.uri", "file:///C:/Clients/Secret.ipt")
			.SetTag("file.path", @"C:\Clients\Secret.ipt")
			.SetTag("gen_ai.tool.call.arguments", "{\"code\":\"secret\"}")
			.SetStatus(ActivityStatusCode.Error, "[{\"type\":\"text\",\"text\":\"secret result\"}]");

		new AllowlistProcessor().OnEnd(span);

		Assert.Equal(
			[TelemetryTags.ToolName, TelemetryTags.RequestId],
			span.TagObjects.Select(static tag => tag.Key).Order(StringComparer.Ordinal));
		Assert.Equal(ActivityStatusCode.Error, span.Status);
		Assert.Null(span.StatusDescription);
	}

	[Fact]
	public void MetricsHaveNoIdOfASessionOrARequest()
	{
		Assert.DoesNotContain(TelemetryTags.SessionId, TelemetryTags.AllowedOnMetrics);
		Assert.DoesNotContain(TelemetryTags.RequestId, TelemetryTags.AllowedOnMetrics);
		Assert.Contains(TelemetryTags.ToolName, TelemetryTags.AllowedOnMetrics);
	}

	[Fact]
	public void DurationViewUsesTheBucketsOfTheConventions()
	{
		System.Diagnostics.Metrics.Meter meter = new("InventorMcp.Server.Tests.View");
		System.Diagnostics.Metrics.Histogram<double> duration = meter.CreateHistogram<double>("test.duration", "s");
		System.Diagnostics.Metrics.Counter<long> counter = meter.CreateCounter<long>("test.count");

		ExplicitBucketHistogramConfiguration histogram = Assert.IsType<ExplicitBucketHistogramConfiguration>(TelemetryRegistration.MetricView(duration));
		Assert.Equal(ServerTelemetry.DurationBoundaries, histogram.Boundaries);
		Assert.Equal(TelemetryTags.AllowedOnMetrics, histogram.TagKeys);

		MetricStreamConfiguration count = TelemetryRegistration.MetricView(counter);
		Assert.IsNotType<ExplicitBucketHistogramConfiguration>(count);
		Assert.Equal(TelemetryTags.AllowedOnMetrics, count.TagKeys);

		meter.Dispose();
	}
}
