using System.Globalization;
using System.Text.Json;

using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;
using InventorMcp.Server.Telemetry;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

/// <summary>
/// 	The tools of the server and what happens around each tool call, the same for <c>Program.cs</c> and the tests.
/// </summary>
internal static class ToolPipeline
{
	/// <summary>
	/// 	The tools that the read-only tool set keeps although they are not read-only.
	/// </summary>
	/// <remarks>
	/// 	They select and start the Inventor session, and change no document.
	/// </remarks>
	private static readonly HashSet<string> _sessionTools = new(StringComparer.Ordinal) { "inventor_start", "inventor_use_release" };

	/// <summary>
	/// 	The tools that answer a dialog or close documents, which the user confirms when the setting is on.
	/// </summary>
	private static readonly HashSet<string> _confirmedTools = new(StringComparer.Ordinal) { "inventor_dialog_click", "inventor_close_documents" };

	/// <summary>
	/// 	The argument that removes the guard for unsaved changes.
	/// </summary>
	private const string AllowUnsavedChangesArgument = "allowUnsavedChanges";

	/// <summary>
	/// 	Registers the services that the tools take as parameters.
	/// </summary>
	/// <remarks>
	/// 	The MCP library leaves a parameter out of the input schema of a tool only when its type is a registered
	/// 	service, so the tests must register the same services as the server.
	/// </remarks>
	/// <param name="services">
	/// 	The services of the host.
	/// </param>
	/// <param name="skillCatalog">
	/// 	The catalog that the server also gives to the Skills over MCP extension.
	/// </param>
	/// <param name="dialogSettings">
	/// 	Gives the dialog settings, read one time at start.
	/// </param>
	/// <returns>
	/// 	The same services.
	/// </returns>
	public static IServiceCollection AddInventorServices(this IServiceCollection services, SkillCatalog skillCatalog, Func<DialogSettings> dialogSettings) =>
		services
			.AddSingleton<IBlockingDialogs>(static _ => new BlockingDialogs())
			.AddSingleton(TimeProvider.System)
			.AddSingleton<ReleaseSelection>()
			.AddSingleton(provider => new BridgeClient(
				provider.GetRequiredService<ILogger<BridgeClient>>(),
				provider.GetRequiredService<IBlockingDialogs>(),
				provider.GetRequiredService<TimeProvider>(),
				provider.GetRequiredService<ReleaseSelection>(),
				dialogSettings))
			// Singleton because it indexes a 12 MB documentation file once and holds the result.
			.AddSingleton<ApiReferenceService>()
			.AddSingleton(skillCatalog)
			.AddSingleton<InventorInstallations>();

	/// <summary>
	/// 	Registers the tools that the settings permit.
	/// </summary>
	/// <remarks>
	/// 	The read-only tool set is a limit on what a model can ask for.
	/// 	It is not a sandbox: a read-only tool still runs its own snippet in Inventor.
	/// </remarks>
	/// <param name="builder">
	/// 	The MCP server builder.
	/// </param>
	/// <param name="settings">
	/// 	The limits from the environment.
	/// </param>
	/// <returns>
	/// 	The same builder.
	/// </returns>
	public static IMcpServerBuilder WithInventorTools(this IMcpServerBuilder builder, SafetySettings settings)
	{
		_ = builder.WithToolsFromAssembly(typeof(InventorTool).Assembly);

		if (settings.ReadOnlyTools)
		{
			// After the library fills the collection from the registered tools.
			_ = builder.Services.PostConfigure<McpServerOptions>(static options =>
			{
				if (options.ToolCollection is not { } tools)
					return;

				foreach (McpServerTool tool in tools.ToArray().Where(static tool => IsInReadOnlySet(tool.ProtocolTool) is false))
					_ = tools.Remove(tool);
			});
		}

		return builder;
	}

	/// <summary>
	/// 	Says if the read-only tool set has a tool.
	/// </summary>
	internal static bool IsInReadOnlySet(Tool tool) =>
		tool.Annotations?.ReadOnlyHint is true || _sessionTools.Contains(tool.Name);

	/// <summary>
	/// 	Adds what the server does around each tool call.
	/// </summary>
	/// <remarks>
	/// 	The client name goes to the add-in's audit log, so each snippet there shows which client sent it.
	/// 	The release of each call goes to the server log, because a session can change its release, and the log of the
	/// 	MCP library does not name the tool.
	/// 	A result that reports a failure gets <c>isError</c>, so a client can show and count it.
	/// </remarks>
	/// <param name="builder">
	/// 	The MCP server builder.
	/// </param>
	/// <param name="settings">
	/// 	The limits from the environment.
	/// </param>
	/// <param name="finished">
	/// 	Called after each call with the tool name, the release and how the session chose it, for the server log.
	/// </param>
	/// <returns>
	/// 	The same builder.
	/// </returns>
	public static IMcpServerBuilder WithInventorCallFilter(
		this IMcpServerBuilder builder,
		SafetySettings settings,
		Action<string?, string, string>? finished = null) =>
		builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
		{
			BridgeClient bridge = context.Services!.GetRequiredService<BridgeClient>();
			ReleaseSelection selection = context.Services!.GetRequiredService<ReleaseSelection>();

			if (bridge.ClientName is null && context.Server.ClientInfo is { } client)
				bridge.ClientName = ExecutionAuditLog.OneLine($"{client.Name} {client.Version}").Trim();

			try
			{
				// The client writes the name. A name that matched no tool is not exported.
				CallToolResult result = await ServerTelemetry.RunToolCallAsync(
					async () => settings.ConfirmRiskyCalls && await RefusalAsync(context, cancellationToken).ConfigureAwait(false) is { } refusal
						? refusal
						: await next(context, cancellationToken).ConfigureAwait(false),
					context.MatchedPrimitive is null ? null : context.Params?.Name,
					() => (bridge.ReleaseYear, bridge.ClientName, selection.Source)).ConfigureAwait(false);

				return MarkFailure(result);
			}
			finally
			{
				finished?.Invoke(
					context.Params?.Name,
					bridge.ReleaseYear?.ToString(CultureInfo.InvariantCulture) ?? "not connected",
					selection.Source);
			}
		}));

	/// <summary>
	/// 	Sets <c>isError</c> on a result that reports a failure.
	/// </summary>
	/// <remarks>
	/// 	The tools return a failure as a normal payload, so the model can read its code and message.
	/// 	The MCP specification also wants <c>isError</c> on it. The payload does not change.
	/// </remarks>
	/// <param name="result">
	/// 	The result of a tool.
	/// </param>
	/// <returns>
	/// 	The same result.
	/// </returns>
	internal static CallToolResult MarkFailure(CallToolResult result)
	{
		if (result.IsError is not true && ReportsFailure(result))
			result.IsError = true;

		return result;
	}

	/// <summary>
	/// 	Says if a result is a failure: an <c>error</c> code at the top level, or code that did not succeed.
	/// </summary>
	/// <remarks>
	/// 	An <c>error</c> inside a list item is part of a result that worked, ex. one file of several, so only the top
	/// 	level counts. A result with <c>blockingDialogs</c> holds the result of the tool one level down, in <c>result</c>.
	/// </remarks>
	internal static bool ReportsFailure(CallToolResult result)
	{
		foreach (ContentBlock block in result.Content)
		{
			if (block is not TextContentBlock { Text: { } text } || text.StartsWith('{') is false)
				continue;

			try
			{
				using JsonDocument document = JsonDocument.Parse(text);
				JsonElement root = document.RootElement;

				if (IsFailure(root) || (root.TryGetProperty("result", out JsonElement inner) && inner.ValueKind is JsonValueKind.Object && IsFailure(inner)))
					return true;
			}
			catch (JsonException)
			{
				// Text that only looks like JSON is not a failure.
			}
		}

		return false;
	}

	private static bool IsFailure(JsonElement payload) =>
		(payload.TryGetProperty("error", out JsonElement error) && error.ValueKind is JsonValueKind.String)
		|| (payload.TryGetProperty("succeeded", out JsonElement succeeded) && succeeded.ValueKind is JsonValueKind.False)
		// The spelling when a tool gives the result of the add-in with no change.
		|| (payload.TryGetProperty("Succeeded", out JsonElement succeededPascal) && succeededPascal.ValueKind is JsonValueKind.False);

	/// <summary>
	/// 	Says if the user must confirm a call.
	/// </summary>
	internal static bool IsRisky(CallToolRequestParams? call) =>
		call is not null
		&& (_confirmedTools.Contains(call.Name)
			|| (call.Arguments is { } arguments
				&& arguments.TryGetValue(AllowUnsavedChangesArgument, out JsonElement allow)
				&& allow.ValueKind is JsonValueKind.True));

	/// <summary>
	/// 	Asks the user to confirm a risky call.
	/// </summary>
	/// <returns>
	/// 	Null when the call can run, or the result to return in its place.
	/// </returns>
	private static async ValueTask<CallToolResult?> RefusalAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
	{
		if (context.MatchedPrimitive is null || IsRisky(context.Params) is false)
			return null;

		string tool = context.Params.Name;

		if (context.Server.ClientCapabilities?.Elicitation is null)
		{
			return Refusal(new
			{
				error = "confirmation-not-supported",
				message = $"The user set {SafetySettings.ConfirmVariable}, so the user must confirm this call, but this client cannot show a question. " +
					"Nothing was done. Tell the user. Do not try another tool to get the same effect."
			});
		}

		ElicitResult answer;

		try
		{
			answer = await context.Server.ElicitAsync(
				new ElicitRequestParams
				{
					// Only the tool name and fixed text. An argument can hold any text, so it is not shown as a question.
					Message = _confirmedTools.Contains(tool)
						? $"The model wants to call {tool} in Inventor. Allow this call?"
						: $"The model wants to call {tool} with {AllowUnsavedChangesArgument}, so it can change a document that has unsaved changes. Allow this call?",
					RequestedSchema = new ElicitRequestParams.RequestSchema
					{
						Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
					}
				},
				cancellationToken).ConfigureAwait(false);
		}
		catch (McpException)
		{
			return Refusal(new
			{
				error = "confirmation-not-supported",
				message = "The client did not answer the question that confirms this call. Nothing was done. Tell the user."
			});
		}

		return answer.IsAccepted
			? null
			: Refusal(new
			{
				error = "declined-by-user",
				message = "The user did not allow this call. Nothing was done. Do not try another tool to get the same effect."
			});
	}

	private static CallToolResult Refusal(object payload) => new()
	{
		Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, McpJsonUtilities.DefaultOptions) }],
		IsError = true
	};
}
