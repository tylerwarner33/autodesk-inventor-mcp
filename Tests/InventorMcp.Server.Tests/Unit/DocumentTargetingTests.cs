using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;
using InventorMcp.Server.McpTools;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	A tool sends its documentName to the add-in, and opens a file only when the add-in does not find it and the tool may.
/// </summary>
[Trait("Level", "Unit")]
public sealed class DocumentTargetingTests : IDisposable
{
	private readonly string _file = Path.Combine(Path.GetTempPath(), $"InventorMcp.Test.{Guid.NewGuid():N}.ipt");

	public DocumentTargetingTests() => File.WriteAllText(_file, "not a real part");

	public void Dispose() => File.Delete(_file);

	[Fact]
	public async Task TheToolSendsItsDocumentNameToTheAddIn()
	{
		await using TestBridge bridge = new();
		Task<TestConnection> accept = bridge.Server.AcceptAsync();
		Task<object> call = InventorTool.ILogicRulesAsync(bridge.Client, "Bracket.ipt (State2)", TestContext.Current.CancellationToken);
		TestConnection connection = await accept;

		TestRequest request = await connection.ReadRequestAsync();
		await connection.RespondAsync(request.Id, Succeeded("""{"document":"Bracket.ipt","rules":[]}"""));
		_ = await call;

		Assert.Equal(BridgeOperations.EvalCSharp, request.Operation);
		Assert.Equal("Bracket.ipt (State2)", request.Payload!.Value.GetProperty("documentName").GetString());
	}

	[Fact]
	public async Task AFileThatIsNotOpenIsOpenedOnASecondSend()
	{
		await using TestBridge bridge = new();
		Task<TestConnection> accept = bridge.Server.AcceptAsync();
		Task<object> call = InventorTool.ILogicRulesAsync(bridge.Client, _file, TestContext.Current.CancellationToken);
		TestConnection connection = await accept;

		TestRequest first = await connection.ReadRequestAsync();
		await connection.FailAsync(first.Id, BridgeErrorCodes.NotFound, "No open document matches.");

		TestRequest second = await connection.ReadRequestAsync();
		await connection.RespondAsync(second.Id, Succeeded("""{"document":"x","rules":[]}"""));
		_ = await call;

		// The second send names no document, and its code opens the file.
		Assert.False(second.Payload!.Value.TryGetProperty("documentName", out _));
		Assert.Contains("FindOrOpenToolDocument(", second.Payload.Value.GetProperty("code").GetString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task AWriteDoesNotOpenAFile()
	{
		await using TestBridge bridge = new();
		Task<TestConnection> accept = bridge.Server.AcceptAsync();
		Task<object> call = InventorTool.SetParametersAsync(
			bridge.Client,
			new Dictionary<string, string>(StringComparer.Ordinal) { ["Width"] = "50 mm" },
			_file,
			cancellationToken: TestContext.Current.CancellationToken);
		TestConnection connection = await accept;

		TestRequest first = await connection.ReadRequestAsync();
		await connection.FailAsync(first.Id, BridgeErrorCodes.NotFound, "No open document matches.");

		string result = System.Text.Json.JsonSerializer.Serialize(await call);

		Assert.Contains(BridgeErrorCodes.NotFound, result, StringComparison.Ordinal);
		Assert.Contains("is not open", result, StringComparison.Ordinal);
		_ = await Assert.ThrowsAsync<TimeoutException>(() => connection.ReadRequestAsync(TimeSpan.FromMilliseconds(500)));
	}

	[Fact]
	public async Task AnAmbiguousNameReachesTheModelWithItsCode()
	{
		await using TestBridge bridge = new();
		Task<TestConnection> accept = bridge.Server.AcceptAsync();
		Task<object> call = InventorTool.ILogicRulesAsync(bridge.Client, "Bracket.ipt", TestContext.Current.CancellationToken);
		TestConnection connection = await accept;

		TestRequest request = await connection.ReadRequestAsync();
		await connection.FailAsync(request.Id, BridgeErrorCodes.AmbiguousDocument, "'Bracket.ipt' matches 2 open documents.");

		Assert.Contains(BridgeErrorCodes.AmbiguousDocument, System.Text.Json.JsonSerializer.Serialize(await call), StringComparison.Ordinal);
	}

	private static ExecutionResult Succeeded(string json) => new(true, json, "String", [], [], null, null, 1);
}
