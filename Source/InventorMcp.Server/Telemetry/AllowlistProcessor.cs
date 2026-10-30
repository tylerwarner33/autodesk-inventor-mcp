using System.Diagnostics;

using OpenTelemetry;

namespace InventorMcp.Server.Telemetry;

/// <summary>
/// 	Removes each span tag that is not in <see cref="TelemetryTags.Allowed"/>, and the status text, before the export.
/// </summary>
/// <remarks>
/// 	It covers the spans of the MCP library too, whose tags the server does not control (ex. <c>mcp.resource.uri</c>).
/// 	The library puts the content of an <c>isError</c> result in the status text, so the text goes too.
/// 	The library also names its span after the method and the tool or prompt of the request, which the client writes,
/// 	so the span gets a name from known values only.
/// 	The Collector in <c>Tools/Telemetry</c> applies the same list again.
/// </remarks>
internal sealed class AllowlistProcessor : BaseProcessor<Activity>
{
	public override void OnEnd(Activity data)
	{
		List<string>? removed = null;

		foreach (KeyValuePair<string, object?> tag in data.TagObjects)
		{
			if (TelemetryTags.Allowed.Contains(tag.Key) is false)
				(removed ??= []).Add(tag.Key);
		}

		// SetTag removes one tag with the key, and the library can add a key twice (ex. error.type).
		if (removed is not null)
		{
			foreach (string key in removed)
				_ = data.SetTag(key, null);
		}

		if (data.Source.Name == ServerTelemetry.LibrarySourceName)
			NameLibrarySpan(data);

		if (data.StatusDescription is not null)
			_ = data.SetStatus(data.Status);
	}

	/// <summary>
	/// 	Gives a span of the MCP library the name <c>method</c>, or <c>tools/call tool</c>, from known values only.
	/// </summary>
	private static void NameLibrarySpan(Activity span)
	{
		string method = TelemetryTags.MethodNameValue(span.GetTagItem(TelemetryTags.MethodName) as string);
		Replace(span, TelemetryTags.MethodName, method);

		if (span.GetTagItem(TelemetryTags.ToolName) is null)
		{
			span.DisplayName = method;
			return;
		}

		// Set by ServerTelemetry.RecordToolCall. A span with no value did not reach the call tool filter.
		string tool = span.GetCustomProperty(ServerTelemetry.ToolNameProperty) as string ?? TelemetryTags.Other;
		Replace(span, TelemetryTags.ToolName, tool);
		span.DisplayName = $"{method} {tool}";
	}

	/// <summary>
	/// 	Gives each tag with the key one value, if the span has the key.
	/// </summary>
	private static void Replace(Activity span, string key, string value)
	{
		if (span.GetTagItem(key) is null)
			return;

		while (span.GetTagItem(key) is not null)
			_ = span.SetTag(key, null);

		_ = span.SetTag(key, value);
	}
}
