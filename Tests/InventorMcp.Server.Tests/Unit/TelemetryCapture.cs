using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

using InventorMcp.Server.Telemetry;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	Records the spans and the measurements of the server and of the MCP library, as an exporter would see them.
/// </summary>
/// <remarks>
/// 	The tests run in parallel, and the sources and meters are static, so a test sees the data of other tests too.
/// 	Each test starts its own <see cref="Root"/> span and reads only the spans of that trace, and finds its
/// 	measurements by a value that only it uses (ex. a tool name).
/// </remarks>
internal sealed class TelemetryCapture : IDisposable
{
	/// <summary>
	/// 	The source of the root span of each test.
	/// </summary>
	public static readonly ActivitySource TestSource = new(TestSourceName);

	private const string TestSourceName = "InventorMcp.Server.Tests";

	private readonly ActivityListener _activities;
	private readonly MeterListener _meters;
	private readonly ConcurrentQueue<Activity> _stopped = new();
	private readonly ConcurrentQueue<CapturedMeasurement> _measurements = new();

	public TelemetryCapture()
	{
		_activities = new ActivityListener
		{
			// By name: a source that is first created in this callback does not get the listener.
			ShouldListenTo = static source => source.Name is ServerTelemetry.Name or ServerTelemetry.LibrarySourceName or TestSourceName,
			Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = _stopped.Enqueue
		};

		ActivitySource.AddActivityListener(_activities);

		_meters = new MeterListener
		{
			InstrumentPublished = static (instrument, listener) =>
			{
				if (instrument.Meter.Name is ServerTelemetry.Name or ServerTelemetry.LibrarySourceName)
					listener.EnableMeasurementEvents(instrument);
			}
		};

		_meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
		_meters.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument, value, tags));
		_meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
		_meters.Start();

		Root = TestSource.StartActivity("test root")!;
	}

	/// <summary>
	/// 	The span that each span of this test is under.
	/// </summary>
	public Activity Root { get; }

	/// <summary>
	/// 	The spans of this test that ended, in order.
	/// </summary>
	public IReadOnlyList<Activity> Spans => [.. _stopped.Where(span => span.TraceId == Root.TraceId && span != Root)];

	/// <summary>
	/// 	The measurements of one instrument, from every test.
	/// </summary>
	public IReadOnlyList<CapturedMeasurement> Measurements(string instrument) =>
		[.. _measurements.Where(measurement => measurement.Instrument == instrument)];

	/// <summary>
	/// 	Reads the observable instruments now.
	/// </summary>
	public void Observe() => _meters.RecordObservableInstruments();

	public void Dispose()
	{
		Root.Dispose();
		_meters.Dispose();
		_activities.Dispose();
	}

	private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
		_measurements.Enqueue(new CapturedMeasurement(
			instrument.Name,
			instrument.Meter.Name,
			instrument.Unit,
			value,
			tags.ToArray().ToDictionary(static tag => tag.Key, static tag => tag.Value?.ToString())));
}

/// <summary>
/// 	One measurement.
/// </summary>
internal sealed record CapturedMeasurement(string Instrument, string Meter, string? Unit, double Value, Dictionary<string, string?> Tags)
{
	public string? Tag(string key) => Tags.GetValueOrDefault(key);
}
