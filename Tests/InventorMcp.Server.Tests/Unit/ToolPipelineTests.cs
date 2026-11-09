using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

using InventorMcp.Server.McpTools;
using InventorMcp.Server.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The tools as a client sees them, through the same registration as <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// 	No test here calls a tool that uses the bridge, because the bridge of the host would find a real Inventor.
/// </remarks>
[Trait("Level", "Unit")]
public sealed class ToolPipelineTests
{
	private static readonly SafetySettings _allTools = new(ReadOnlyTools: false, ConfirmRiskyCalls: false);

	private static readonly string[] _documentNames = ["Part1.ipt"];

	/// <summary>
	/// 	The tool list is the public API of the server: a change of a name, a hint or a parameter changes what every
	/// 	client and every saved permission sees.
	/// </summary>
	[Fact]
	public async Task ToolListIsTheSameAsTheSnapshot()
	{
		await using ToolServer server = await ToolServer.StartAsync(_allTools);

		string actual = Describe(await server.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken));
		string snapshotPath = Path.Combine(AppContext.BaseDirectory, "Unit", "Snapshots", "ToolList.txt");
		string expected = (await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");

		if (string.Equals(expected, actual, StringComparison.Ordinal) is false)
		{
			string actualPath = Path.Combine(AppContext.BaseDirectory, "ToolList.actual.txt");
			await File.WriteAllTextAsync(actualPath, actual, TestContext.Current.CancellationToken);

			Assert.Fail(
				"The tool list changed. If the change is intended, copy 'ToolList.actual.txt' from the test output folder over " +
				"'Tests/InventorMcp.Server.Tests/Unit/Snapshots/ToolList.txt', and add the change to CHANGELOG.md.\n" +
				FirstDifference(expected, actual));
		}
	}

	[Fact]
	public async Task EveryToolSaysWhatItCanChange()
	{
		await using ToolServer server = await ToolServer.StartAsync(_allTools);

		foreach (McpClientTool tool in await server.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
		{
			ToolAnnotations? annotations = tool.ProtocolTool.Annotations;

			Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Title), $"{tool.Name} has no title.");
			Assert.True(annotations?.ReadOnlyHint is not null, $"{tool.Name} does not set ReadOnly.");
			Assert.True(annotations?.OpenWorldHint is not null, $"{tool.Name} does not set OpenWorld.");

			// The specification reads a missing hint on a tool that writes as destructive, so the tool says it.
			if (annotations?.ReadOnlyHint is false)
				Assert.True(annotations.DestructiveHint is not null, $"{tool.Name} writes and does not set Destructive.");
		}
	}

	[Fact]
	public async Task ReadOnlyToolSetHasNoToolThatWrites()
	{
		await using ToolServer server = await ToolServer.StartAsync(new SafetySettings(ReadOnlyTools: true, ConfirmRiskyCalls: false));

		IList<McpClientTool> tools = await server.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
		string[] names = [.. tools.Select(static tool => tool.Name)];

		Assert.All(tools, static tool => Assert.True(ToolPipeline.IsInReadOnlySet(tool.ProtocolTool), $"{tool.Name} is not in the read-only set."));
		Assert.Contains("inventor_session", names);
		Assert.Contains("inventor_start", names);
		Assert.Contains("inventor_skill", names);
		Assert.DoesNotContain("inventor_eval_csharp", names);
		Assert.DoesNotContain("inventor_run_ilogic", names);
		Assert.DoesNotContain("inventor_run_plugin", names);
		Assert.DoesNotContain("inventor_set_parameter", names);
		Assert.DoesNotContain("inventor_dialog_click", names);
		Assert.DoesNotContain("inventor_close_documents", names);
	}

	[Fact]
	public async Task ToolOutsideTheReadOnlySetCannotBeCalled()
	{
		await using ToolServer server = await ToolServer.StartAsync(new SafetySettings(ReadOnlyTools: true, ConfirmRiskyCalls: false));

		// The library answers an unknown tool with a protocol error or an error result, and never runs it.
		try
		{
			CallToolResult result = await server.Client.CallToolAsync(
				"inventor_close_documents",
				new Dictionary<string, object?>(StringComparer.Ordinal) { ["documentNames"] = Array.Empty<string>() },
				cancellationToken: TestContext.Current.CancellationToken);

			Assert.True(result.IsError);
		}
		catch (ModelContextProtocol.McpException)
		{
			// A protocol error is also a refusal.
		}
	}

	[Fact]
	public async Task FailureOfAToolSetsIsErrorAndASuccessDoesNot()
	{
		await using ToolServer server = await ToolServer.StartAsync(_allTools);

		CallToolResult failed = await server.Client.CallToolAsync(
			"inventor_skill",
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["action"] = "no-such-action" },
			cancellationToken: TestContext.Current.CancellationToken);

		CallToolResult worked = await server.Client.CallToolAsync(
			"inventor_skill",
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["action"] = "list" },
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(failed.IsError);
		Assert.Contains("invalid-arguments", Assert.IsType<TextContentBlock>(failed.Content[0]).Text, StringComparison.Ordinal);
		Assert.NotEqual(true, worked.IsError);
	}

	[Theory]
	[InlineData("""{"error":"not-found","message":"No document."}""", true)]
	[InlineData("""{"succeeded":false,"returnValue":null,"output":[]}""", true)]
	[InlineData("""{"result":{"succeeded":false},"blockingDialogs":[{"title":"iLogic"}]}""", true)]
	[InlineData("""{"result":{"error":"busy"},"blockingDialogs":[]}""", true)]
	[InlineData("""{"succeeded":true,"returnValue":"11","output":[]}""", false)]
	[InlineData("""{"files":[{"path":"a.ipt","error":"not-found"}]}""", false)]
	[InlineData("""{"error":null,"value":1}""", false)]
	[InlineData("{not json", false)]
	[InlineData("plain text with \"error\"", false)]
	public void OnlyAFailureAtTheTopLevelIsAFailure(string text, bool failure)
	{
		CallToolResult result = ToolPipeline.MarkFailure(new CallToolResult { Content = [new TextContentBlock { Text = text }] });

		Assert.Equal(failure, result.IsError is true);
	}

	[Theory]
	[InlineData("inventor_dialog_click", "{}", true)]
	[InlineData("inventor_close_documents", "{}", true)]
	[InlineData("inventor_eval_csharp", """{"code":"return 1;","allowUnsavedChanges":true}""", true)]
	[InlineData("inventor_eval_csharp", """{"code":"return 1;","allowUnsavedChanges":false}""", false)]
	[InlineData("inventor_eval_csharp", """{"code":"return 1;"}""", false)]
	[InlineData("inventor_session", "{}", false)]
	public void RiskyCallsAreTheGuardRemovalTheDialogClickAndTheClose(string tool, string arguments, bool risky)
	{
		CallToolRequestParams call = new()
		{
			Name = tool,
			Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments)
		};

		Assert.Equal(risky, ToolPipeline.IsRisky(call));
	}

	[Fact]
	public async Task RiskyCallThatTheUserDeclinesDoesNotRun()
	{
		List<string> questions = [];

		await using ToolServer server = await ToolServer.StartAsync(
			new SafetySettings(ReadOnlyTools: false, ConfirmRiskyCalls: true),
			(request, _) =>
			{
				questions.Add(request?.Message ?? "");

				return ValueTask.FromResult(new ElicitResult { Action = "decline" });
			});

		CallToolResult result = await server.Client.CallToolAsync(
			"inventor_close_documents",
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["documentNames"] = _documentNames },
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(result.IsError);
		Assert.Contains("declined-by-user", Assert.IsType<TextContentBlock>(result.Content[0]).Text, StringComparison.Ordinal);

		string question = Assert.Single(questions);
		Assert.Contains("inventor_close_documents", question, StringComparison.Ordinal);
		// The question shows no argument, which a model can fill with any text.
		Assert.DoesNotContain("Part1.ipt", question, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RiskyCallIsRefusedWhenTheClientCannotAsk()
	{
		await using ToolServer server = await ToolServer.StartAsync(new SafetySettings(ReadOnlyTools: false, ConfirmRiskyCalls: true));

		CallToolResult result = await server.Client.CallToolAsync(
			"inventor_dialog_click",
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["handle"] = "0x1", ["button"] = "OK" },
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(result.IsError);
		Assert.Contains("confirmation-not-supported", Assert.IsType<TextContentBlock>(result.Content[0]).Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task CallThatIsNotRiskyAsksNothing()
	{
		int questions = 0;

		await using ToolServer server = await ToolServer.StartAsync(
			new SafetySettings(ReadOnlyTools: false, ConfirmRiskyCalls: true),
			(_, _) =>
			{
				questions++;

				return ValueTask.FromResult(new ElicitResult { Action = "decline" });
			});

		CallToolResult result = await server.Client.CallToolAsync(
			"inventor_skill",
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["action"] = "list" },
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.NotEqual(true, result.IsError);
		Assert.Equal(0, questions);
	}

	[Theory]
	[InlineData(null, null, false, false, false)]
	[InlineData("all", "false", false, false, false)]
	[InlineData("read-only", "true", true, true, false)]
	[InlineData(" Read-Only ", " TRUE ", true, true, false)]
	[InlineData("readonly", null, true, false, true)]
	[InlineData(null, "yes", false, true, true)]
	public void UnknownSafetyValueSelectsTheStricterSetting(string? tools, string? confirm, bool readOnly, bool confirms, bool problem)
	{
		SafetySettings settings = SafetySettings.Read(name => name switch
		{
			SafetySettings.ToolsVariable => tools,
			SafetySettings.ConfirmVariable => confirm,
			_ => null
		});

		Assert.Equal(readOnly, settings.ReadOnlyTools);
		Assert.Equal(confirms, settings.ConfirmRiskyCalls);
		Assert.Equal(problem, settings.Problem is not null);
	}

	/// <summary>
	/// 	One line for each tool: its name, title, hints and parameters, with <c>*</c> on a required parameter.
	/// </summary>
	private static string Describe(IEnumerable<McpClientTool> tools)
	{
		StringBuilder text = new();

		foreach (Tool tool in tools.Select(static tool => tool.ProtocolTool).OrderBy(static tool => tool.Name, StringComparer.Ordinal))
		{
			HashSet<string> required = tool.InputSchema.TryGetProperty("required", out JsonElement requiredNames)
				? [.. requiredNames.EnumerateArray().Select(static name => name.GetString()!)]
				: [];

			IEnumerable<string> parameters = tool.InputSchema.TryGetProperty("properties", out JsonElement properties)
				? properties.EnumerateObject().Select(property => required.Contains(property.Name) ? $"{property.Name}*" : property.Name)
				: [];

			ToolAnnotations? annotations = tool.Annotations;

			_ = text
				.Append(tool.Name).Append(" | ").Append(tool.Title)
				.Append(" | readOnly=").Append(Hint(annotations?.ReadOnlyHint))
				.Append(" destructive=").Append(Hint(annotations?.DestructiveHint))
				.Append(" idempotent=").Append(Hint(annotations?.IdempotentHint))
				.Append(" openWorld=").Append(Hint(annotations?.OpenWorldHint))
				.Append(" | ").AppendJoin(", ", parameters)
				.Append('\n');
		}

		return text.ToString();
	}

	private static string Hint(bool? hint) => hint switch { true => "true", false => "false", _ => "-" };

	private static string FirstDifference(string expected, string actual)
	{
		string[] expectedLines = expected.Split('\n');
		string[] actualLines = actual.Split('\n');

		for (int index = 0; index < Math.Max(expectedLines.Length, actualLines.Length); index++)
		{
			string expectedLine = index < expectedLines.Length ? expectedLines[index] : "(no line)";
			string actualLine = index < actualLines.Length ? actualLines[index] : "(no line)";

			if (string.Equals(expectedLine, actualLine, StringComparison.Ordinal) is false)
				return $"First difference, line {index + 1}:\nSnapshot: {expectedLine}\nNow:      {actualLine}";
		}

		return "";
	}

	/// <summary>
	/// 	An MCP server with the tools and the call filter of <c>Program.cs</c>, and a client, in memory.
	/// </summary>
	private sealed class ToolServer(IHost host, McpClient client) : IAsyncDisposable
	{
		public McpClient Client { get; } = client;

		public static async Task<ToolServer> StartAsync(
			SafetySettings settings,
			Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>>? answer = null)
		{
			Pipe toServer = new();
			Pipe toClient = new();

			HostApplicationBuilder builder = Host.CreateApplicationBuilder();
			_ = builder.Logging.ClearProviders();
			_ = builder.Services.AddInventorServices(new SkillCatalog(), static () => TestDialogSettings.TrustsRules);

			_ = builder.Services.AddMcpServer()
				.WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
				.WithInventorTools(settings)
				.WithInventorCallFilter(settings);

			IHost host = builder.Build();
			await host.StartAsync(TestContext.Current.CancellationToken);

			McpClientOptions options = new();

			if (answer is not null)
				options.Handlers = new McpClientHandlers { ElicitationHandler = answer };

			McpClient client = await McpClient.CreateAsync(
				new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
				options,
				cancellationToken: TestContext.Current.CancellationToken);

			return new ToolServer(host, client);
		}

		public async ValueTask DisposeAsync()
		{
			await Client.DisposeAsync();
			await host.StopAsync(TestContext.Current.CancellationToken);
			host.Dispose();
		}
	}
}
