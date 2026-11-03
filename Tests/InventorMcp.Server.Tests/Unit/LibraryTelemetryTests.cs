using System.Diagnostics;
using System.IO.Pipelines;

using InventorMcp.Server.Telemetry;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	What the MCP library records for a tool call, which the server's telemetry depends on.
/// </summary>
/// <remarks>
/// 	The names start with <c>Experimental.</c>, so an update of the library can change them. These tests then fail.
/// 	See <c>Docs/Research/OpenTelemetry-Findings.md</c>, "Phase 0 results".
/// </remarks>
[Trait("Level", "Unit")]
public sealed class LibraryTelemetryTests
{
	[Fact]
	public async Task ServerAddsItsTagsToTheLibrarySpanOfTheCall()
	{
		using TelemetryCapture capture = new();

		await using (InMemoryServer server = await InMemoryServer.StartAsync())
			_ = await server.Client.CallToolAsync("ok_tool", cancellationToken: TestContext.Current.CancellationToken);

		Activity span = Assert.Single(capture.Spans, static span => span.Kind is ActivityKind.Server && span.DisplayName == "tools/call ok_tool");

		Assert.Equal(ServerTelemetry.LibrarySourceName, span.Source.Name);
		Assert.Equal("ok_tool", span.GetTagItem(TelemetryTags.ToolName));
		Assert.Equal(TelemetryTags.OutcomeOk, span.GetTagItem(TelemetryTags.Outcome));
		Assert.Equal("2027", span.GetTagItem(TelemetryTags.Release));
		Assert.NotNull(span.GetTagItem(TelemetryTags.RequestId));

		// The server starts no second span for the same call.
		Assert.DoesNotContain(capture.Spans, static span => span.Source.Name == ServerTelemetry.Name);

		// Each tag of the library is planned for. A new tag of a later version fails here, so the allowlist is looked at.
		Assert.All(span.TagObjects, static tag => Assert.True(TelemetryTags.Allowed.Contains(tag.Key), $"The library tag '{tag.Key}' is not on the allowlist."));
	}

	[Fact]
	public async Task NamesThatTheClientWritesAreNotExported()
	{
		using TelemetryCapture capture = new();
		string secret = $"secret_{Guid.NewGuid():N}";
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;

		await using (InMemoryServer server = await InMemoryServer.StartAsync())
		{
			_ = await Assert.ThrowsAsync<McpProtocolException>(async () => await server.Client.CallToolAsync(secret, cancellationToken: cancellationToken));
			_ = await Assert.ThrowsAsync<McpProtocolException>(async () => await server.Client.GetPromptAsync(secret, cancellationToken: cancellationToken));
			_ = await Assert.ThrowsAsync<McpProtocolException>(async () => await server.Client.SendRequestAsync(new JsonRpcRequest { Method = secret, Id = new RequestId(99) }, cancellationToken));
		}

		Activity[] spans = [.. capture.Spans.Where(span => span.Kind is ActivityKind.Server && span.DisplayName.Contains(secret, StringComparison.Ordinal))];
		Assert.Equal(3, spans.Length);

		AllowlistProcessor allowlist = new();

		foreach (Activity span in spans)
			allowlist.OnEnd(span);

		Assert.Equal(
			[TelemetryTags.Other, "prompts/get", $"{RequestMethods.ToolsCall} {TelemetryTags.Other}"],
			spans.Select(static span => span.DisplayName).Order(StringComparer.Ordinal));
		Assert.All(spans, span => Assert.DoesNotContain(span.TagObjects, tag => tag.Value is string text && text.Contains(secret, StringComparison.Ordinal)));
		Assert.DoesNotContain(capture.Measurements("inventor.tool.calls"), measurement => measurement.Tag(TelemetryTags.ToolName) == secret);

		// The reason that the server does not export the library's meter: a view cannot change this value.
		Assert.Contains(capture.Measurements("mcp.server.operation.duration"), measurement => measurement.Tag(TelemetryTags.ToolName) == secret);
	}

	[Fact]
	public async Task ThrowGivesItsTypeNoEventAndAStatusTextThatTheAllowlistRemoves()
	{
		using TelemetryCapture capture = new();

		await using (InMemoryServer server = await InMemoryServer.StartAsync())
		{
			CallToolResult result = await server.Client.CallToolAsync("throw_tool", cancellationToken: TestContext.Current.CancellationToken);
			Assert.True(result.IsError);
		}

		Activity span = Assert.Single(capture.Spans, static span => span.Kind is ActivityKind.Server && span.DisplayName == "tools/call throw_tool");

		Assert.Empty(span.Events);
		Assert.Equal(ActivityStatusCode.Error, span.Status);
		// The exception passes through the call tool filter, and the library makes the isError result after it. So the
		// server's type name replaces the library's tool_error.
		Assert.Equal("System.InvalidOperationException", span.GetTagItem(TelemetryTags.ErrorType));
		Assert.Equal(TelemetryTags.OutcomeException, span.GetTagItem(TelemetryTags.Outcome));
		Assert.NotNull(span.StatusDescription);

		new AllowlistProcessor().OnEnd(span);
		Assert.Null(span.StatusDescription);
	}

	[Fact]
	public void SerilogTakesTheTraceIdOfTheSpan()
	{
		using TelemetryCapture capture = new();
		LastEvent sink = new();

		using (Logger logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger())
			logger.Information("inside a span");

		Assert.Equal(capture.Root.TraceId, sink.Event?.TraceId);
		Assert.Equal(capture.Root.SpanId, sink.Event?.SpanId);
	}

	/// <summary>
	/// 	An MCP server and a client in memory, with the server's call tool telemetry in the filter.
	/// </summary>
	private sealed class InMemoryServer(IHost host, McpClient client) : IAsyncDisposable
	{
		public McpClient Client { get; } = client;

		public static async Task<InMemoryServer> StartAsync()
		{
			Pipe toServer = new();
			Pipe toClient = new();

			HostApplicationBuilder builder = Host.CreateApplicationBuilder();
			_ = builder.Logging.ClearProviders();

			_ = builder.Services.AddMcpServer()
				.WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
				.WithTools(
				[
					McpServerTool.Create(static () => "ok", new McpServerToolCreateOptions { Name = "ok_tool" }),
					McpServerTool.Create(static string () => throw new InvalidOperationException(@"C:\Clients\Secret.ipt"), new McpServerToolCreateOptions { Name = "throw_tool" })
				])
				// The same as Program.cs.
				.WithRequestFilters(static filters => filters.AddCallToolFilter(static next => (context, cancellationToken) =>
					ServerTelemetry.RunToolCallAsync(
						() => next(context, cancellationToken),
						context.MatchedPrimitive is null ? null : context.Params?.Name,
						static () => (2027, "test-client 1.0", "chosen"))));

			IHost host = builder.Build();
			await host.StartAsync(TestContext.Current.CancellationToken);

			McpClient client = await McpClient.CreateAsync(
				new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
				cancellationToken: TestContext.Current.CancellationToken);

			return new InMemoryServer(host, client);
		}

		public async ValueTask DisposeAsync()
		{
			await Client.DisposeAsync();
			await host.StopAsync(TestContext.Current.CancellationToken);
			host.Dispose();
		}
	}

	private sealed class LastEvent : ILogEventSink
	{
		public LogEvent? Event { get; private set; }

		public void Emit(LogEvent logEvent) => Event = logEvent;
	}
}
