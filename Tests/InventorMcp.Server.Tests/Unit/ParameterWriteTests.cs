using System.Text.Json;

using InventorMcp.Server.McpTools;
using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The snippet of the parameter writes, which groups a call into one Undo step, and the result of a single write.
/// </summary>
[Trait("Level", "Unit")]
public sealed class ParameterWriteTests
{
	private static readonly string[] _hostileNames = ["Width\"; throw new Exception(\"x\"); //", "Line\nBreak"];

	[Fact]
	public void ASnippetThatCallsInTransactionGetsThePrelude()
	{
		Assert.True(ScriptPrelude.IsNeeded("InTransaction(document, \"MCP: Test\", () => { });"));
		Assert.Contains("T InTransaction<T>(object document, string name, Func<T> action)", ScriptPrelude.Apply("InTransaction(document, \"x\", () => { });"), StringComparison.Ordinal);
	}

	[Fact]
	public void TheSnippetWritesInsideOneTransactionAndRestoresTheRulesAfterIt()
	{
		string snippet = Compose(suppressRules: true, runRuleAfter: "Top");

		int transaction = snippet.IndexOf("InTransaction(document, \"MCP: Set Parameters\"", StringComparison.Ordinal);
		int update = snippet.IndexOf("document.Update();", transaction, StringComparison.Ordinal);
		int ruleRun = snippet.IndexOf("automation!.RunRule(document, ruleAfter)", update, StringComparison.Ordinal);
		int restore = snippet.IndexOf("automation!.RulesEnabled = previous;", transaction, StringComparison.Ordinal);

		Assert.InRange(transaction, 1, int.MaxValue);
		Assert.InRange(update, transaction, int.MaxValue);
		// The rule runs inside the step, so one Undo reverts what the rule changed too.
		Assert.InRange(ruleRun, update, int.MaxValue);
		// The rules are a setting of the session, which an abort does not restore, so the restore is after the step.
		Assert.InRange(restore, ruleRun + 1, int.MaxValue);
	}

	[Fact]
	public void TheNamesAndTheValuesAreCSharpLiterals()
	{
		string snippet = ParameterSnippet(
			new Dictionary<string, string>(StringComparer.Ordinal) { [_hostileNames[0]] = "1 mm", [_hostileNames[1]] = "2 \"mm\"\r\n" });

		Assert.Contains(CSharpLiteral.String(_hostileNames[0]), snippet, StringComparison.Ordinal);
		Assert.Contains(CSharpLiteral.String(_hostileNames[1]), snippet, StringComparison.Ordinal);
		Assert.Contains(CSharpLiteral.String("2 \"mm\"\r\n"), snippet, StringComparison.Ordinal);
		// A name that is pasted as text would put its own statement in the snippet.
		Assert.DoesNotContain("\nBreak", snippet, StringComparison.Ordinal);
	}

	[Fact]
	public void ANumericValueIsCheckedBeforeItIsWritten()
	{
		string snippet = Compose();

		Assert.Contains("IsExpressionValid(value, units)", snippet, StringComparison.Ordinal);
		Assert.Contains("State the units explicitly, ex. '50 mm'.", snippet, StringComparison.Ordinal);
	}

	[Fact]
	public void TheSnippetWithNoRuleSettingNeverTouchesTheAutomationObject()
	{
		Assert.Contains("dynamic? automation = null;", Compose(), StringComparison.Ordinal);
		Assert.Contains("dynamic? automation = ILogicAutomation();", Compose(suppressRules: true), StringComparison.Ordinal);
	}

	[Fact]
	public void ASingleWriteReadsAsOneParameter()
	{
		JsonElement batch = Parse("""
			{"document":"C:\\A.ipt","transaction":"MCP: Set Parameters","results":[
				{"name":"Width","ok":true,"created":false,"valueKind":"Numeric","expression":"50 mm","internalValue":5.0,"displayValue":"50 mm","units":"mm"}],
			 "rulesSuppressed":false,"ruleError":null}
			""");

		JsonElement shaped = JsonSerializer.SerializeToElement(InventorTool.ShapeSingleParameter(batch));

		Assert.Equal("Width", shaped.GetProperty("name").GetString());
		Assert.Equal("Numeric", shaped.GetProperty("valueKind").GetString());
		Assert.Equal(5.0, shaped.GetProperty("internalValue").GetDouble());
		Assert.Equal("MCP: Set Parameters", shaped.GetProperty("transaction").GetString());
		Assert.False(shaped.TryGetProperty("ok", out _));
		Assert.False(shaped.TryGetProperty("created", out _));
	}

	[Fact]
	public void AFailedSingleWriteGivesTheCodeAndTheMessage()
	{
		JsonElement batch = Parse("""{"results":[{"name":"Width","ok":false,"code":"not-found","error":"No parameter has this name."}]}""");

		JsonElement shaped = JsonSerializer.SerializeToElement(InventorTool.ShapeSingleParameter(batch));

		Assert.Equal("not-found", shaped.GetProperty("error").GetString());
		Assert.Equal("No parameter has this name.", shaped.GetProperty("message").GetString());
	}

	[Fact]
	public void AReadBackThatFailsAfterTheWriteIsNotAFailedWrite()
	{
		string snippet = Compose();

		// The read back catches its own failure, so the catch of the write does not record it as set-failed.
		Assert.Contains("results.Add(DescribeWritten(parameter, created: false));", snippet, StringComparison.Ordinal);
		Assert.DoesNotContain("results.Add(Describe(", snippet, StringComparison.Ordinal);

		JsonElement batch = Parse("""
			{"document":"C:\\A.ipt","transaction":"MCP: Set Parameters","results":[
				{"name":"Width","ok":true,"created":false,"readBackError":"E_FAIL"}],"rulesSuppressed":false}
			""");

		JsonElement shaped = JsonSerializer.SerializeToElement(InventorTool.ShapeSingleParameter(batch));

		Assert.False(shaped.TryGetProperty("error", out _));
		Assert.Equal("E_FAIL", shaped.GetProperty("readBackError").GetString());
	}

	[Fact]
	public void AFailureOfTheSnippetIsNotChanged()
	{
		JsonElement failure = Parse("""{"error":"execution-failed","message":"boom"}""");

		Assert.Equal(failure.GetRawText(), JsonSerializer.SerializeToElement(InventorTool.ShapeSingleParameter(failure)).GetRawText());
	}

	private static string Compose(bool suppressRules = false, string? runRuleAfter = null) =>
		ParameterSnippet(new Dictionary<string, string>(StringComparer.Ordinal) { ["Width"] = "50 mm" }, suppressRules, runRuleAfter);

	private static string ParameterSnippet(Dictionary<string, string> parameters, bool suppressRules = false, string? runRuleAfter = null) =>
		InventorTool.ComposeSetParametersSnippet(parameters, null, suppressRules, runRuleAfter, createIfMissing: false, allowUnsavedChanges: false);

	private static JsonElement Parse(string json) => JsonElement.Parse(json);
}
