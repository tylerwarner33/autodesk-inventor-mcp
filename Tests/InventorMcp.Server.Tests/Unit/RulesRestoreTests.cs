using System.Text.Json;

using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;
using InventorMcp.Server.McpTools;
using InventorMcp.Server.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	<c>suppressRules</c> on <c>inventor_eval_csharp</c> turns the iLogic rules back on, also after a dialog blocked the
/// 	snippet and the restore.
/// </summary>
[Trait("Level", "Unit")]
public sealed class RulesRestoreTests
{
	private readonly ApiReferenceService _apiReference = new(NullLogger<ApiReferenceService>.Instance);

	[Fact]
	public async Task ABlockedSnippetSaysThatTheRulesAreOffAndTheNextCallTurnsThemOn()
	{
		await using TestBridge bridge = new();
		Task<TestConnection> accept = bridge.Server.AcceptAsync();
		Task<object> call = InventorTool.EvaluateCSharpAsync(bridge.Client, _apiReference, "return 1;", suppressRules: true, cancellationToken: TestContext.Current.CancellationToken);
		TestConnection connection = await accept;

		TestRequest turnOff = await connection.ReadRequestAsync();
		await connection.RespondAsync(turnOff.Id, Succeeded("""{"previous":true}"""));
		TestRequest snippet = await connection.ReadRequestAsync();
		await connection.FailAsync(snippet.Id, BridgeErrorCodes.BlockedByDialog, "Inventor is blocked by a modal dialog.");
		TestRequest restore = await connection.ReadRequestAsync();
		await connection.FailAsync(restore.Id, BridgeErrorCodes.BlockedByDialog, "Inventor is blocked by a modal dialog.");

		string result = JsonSerializer.Serialize(await call);

		Assert.Contains(BridgeErrorCodes.BlockedByDialog, result, StringComparison.Ordinal);
		Assert.Contains("iLogic rules are still turned off", result, StringComparison.Ordinal);
		Assert.True(bridge.Client.OwesILogicRulesRestore);

		// The next call turns the rules on first, then sends its own request.
		Task<SessionInfo> next = bridge.Client.InvokeAsync<SessionInfo>(BridgeOperations.Session, null, TestContext.Current.CancellationToken);
		TestRequest owed = await connection.ReadRequestAsync();
		await connection.RespondAsync(owed.Id, Succeeded("""{"rulesEnabled":true}"""));
		TestRequest session = await connection.ReadRequestAsync();
		await connection.RespondAsync(session.Id, new SessionInfo("2025", 2025, Environment.ProcessId, ActiveDocument: null, OpenDocumentCount: 0, IsBusy: false));
		_ = await next;

		Assert.Equal(BridgeOperations.EvalCSharp, owed.Operation);
		Assert.Contains("automation.RulesEnabled = true;", owed.Payload!.Value.GetProperty("code").GetString(), StringComparison.Ordinal);
		Assert.Equal(BridgeOperations.Session, session.Operation);
		Assert.False(bridge.Client.OwesILogicRulesRestore);
	}

	[Fact]
	public async Task RulesThatWereOffBeforeTheCallNeedNoRestore()
	{
		await using TestBridge bridge = new();
		Task<TestConnection> accept = bridge.Server.AcceptAsync();
		Task<object> call = InventorTool.EvaluateCSharpAsync(bridge.Client, _apiReference, "return 1;", suppressRules: true, cancellationToken: TestContext.Current.CancellationToken);
		TestConnection connection = await accept;

		TestRequest turnOff = await connection.ReadRequestAsync();
		await connection.RespondAsync(turnOff.Id, Succeeded("""{"previous":false}"""));
		TestRequest snippet = await connection.ReadRequestAsync();
		await connection.FailAsync(snippet.Id, BridgeErrorCodes.BlockedByDialog, "Inventor is blocked by a modal dialog.");

		string result = JsonSerializer.Serialize(await call);

		Assert.DoesNotContain("still turned off", result, StringComparison.Ordinal);
		Assert.False(bridge.Client.OwesILogicRulesRestore);
		_ = await Assert.ThrowsAsync<TimeoutException>(() => connection.ReadRequestAsync(TimeSpan.FromMilliseconds(500)));
	}

	private static ExecutionResult Succeeded(string json) => new(true, json, "String", [], [], null, null, 1);
}
