using System.Text.Json;

using InventorMcp.Contracts.Models;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.McpTools;
using InventorMcp.Server.Services;
using InventorMcp.Server.Tests.Desktop;

namespace InventorMcp.Server.Tests.Live;

/// <summary>
/// 	The parameter writes as one Undo step, in a live Inventor.
/// </summary>
/// <remarks>
/// 	Needs Inventor with the add-in loaded. Each test creates a new part, never saves it, and closes it.
/// 	The checks of the transaction rules are in <c>Docs/Research/Write-Transactions-Checks.md</c>.
/// </remarks>
[Trait("Level", "Live")]
[Collection(DesktopCollection.Name)]
public sealed class LiveTransactionTests
{
	private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

	[Fact]
	public async Task TenParametersAreOneNamedUndoStepAndOneUndoRevertsAll()
	{
		LiveInventor.Require();
		await using BridgeClient client = LiveInventor.CreateClient();
		string document = await CreatePartAsync(client, 10);

		try
		{
			Dictionary<string, string> writes = Enumerable.Range(1, 10).ToDictionary(static index => $"Size{index}", static index => $"{index + 20} mm", StringComparer.Ordinal);
			int before = await CommittedCountAsync(client);

			JsonElement result = await SetAsync(client, writes, document);

			Assert.Equal("MCP: Set Parameters", result.GetProperty("transaction").GetString());
			Assert.All(result.GetProperty("results").EnumerateArray(), static entry => Assert.True(entry.GetProperty("ok").GetBoolean()));
			Assert.Equal(before + 1, await CommittedCountAsync(client));
			Assert.Equal("MCP: Set Parameters", await LastTransactionNameAsync(client));
			Assert.Equal(string.Join(",", Enumerable.Range(1, 10).Select(static index => $"{index + 20} mm")), await ExpressionsAsync(client, document));

			await UndoAsync(client);

			Assert.Equal(string.Join(",", Enumerable.Repeat("1 mm", 10)), await ExpressionsAsync(client, document));
		}
		finally
		{
			await ClosePartAsync(client, document);
		}
	}

	[Fact]
	public async Task ABatchWithFailedEntriesKeepsTheSuccessesInOneStep()
	{
		LiveInventor.Require();
		await using BridgeClient client = LiveInventor.CreateClient();
		string document = await CreatePartAsync(client, 3);

		try
		{
			int before = await CommittedCountAsync(client);

			JsonElement result = await SetAsync(
				client,
				new Dictionary<string, string>(StringComparer.Ordinal) { ["Size1"] = "5 mm", ["Size2"] = "not a number", ["Missing"] = "1 mm" },
				document);

			JsonElement[] entries = [.. result.GetProperty("results").EnumerateArray()];

			Assert.True(entries[0].GetProperty("ok").GetBoolean());
			Assert.Equal("invalid-expression", entries[1].GetProperty("code").GetString());
			Assert.Equal("not-found", entries[2].GetProperty("code").GetString());
			Assert.Equal(before + 1, await CommittedCountAsync(client));
			Assert.Equal("5 mm,1 mm,1 mm", await ExpressionsAsync(client, document));
		}
		finally
		{
			await ClosePartAsync(client, document);
		}
	}

	[Fact]
	public async Task AThrowingActionAbortsTheStepAndTheDocumentGoesBack()
	{
		LiveInventor.Require();
		await using BridgeClient client = LiveInventor.CreateClient();
		string document = await CreatePartAsync(client, 1);

		try
		{
			ExecutionResult result = await LiveInventor.EvalAsync(client, ScriptPrelude.Apply($$"""
				Document doc = Application.Documents.Cast<Document>().First(open => open.DisplayName == "{{document}}");
				PartDocument part = (PartDocument)doc;
				int countBefore = Application.TransactionManager.CommittedTransactions.Count;
				try
				{
					InTransaction(doc, "MCP: Test Abort", () =>
					{
						part.ComponentDefinition.Parameters["Size1"].Expression = "9 mm";
						throw new InvalidOperationException("boom");
					});
				}
				catch (InvalidOperationException) { }
				return new { expression = part.ComponentDefinition.Parameters["Size1"].Expression, countBefore, countAfter = Application.TransactionManager.CommittedTransactions.Count };
				"""), Cancellation);

			Assert.True(result.Succeeded, LiveInventor.Describe(result));

			JsonElement json = JsonDocument.Parse(result.ReturnValue!).RootElement;

			Assert.Equal("1 mm", json.GetProperty("expression").GetString());
			Assert.Equal(json.GetProperty("countBefore").GetInt32(), json.GetProperty("countAfter").GetInt32());
		}
		finally
		{
			await ClosePartAsync(client, document);
		}
	}

	[Fact]
	public async Task RulesAreTurnedOnAgainAfterABatchWithAFailedEntry()
	{
		LiveInventor.Require();
		await using BridgeClient client = LiveInventor.CreateClient();
		string document = await CreatePartAsync(client, 2);

		try
		{
			bool before = await RulesEnabledAsync(client);

			JsonElement result = await SetAsync(
				client,
				new Dictionary<string, string>(StringComparer.Ordinal) { ["Size1"] = "6 mm", ["Missing"] = "1 mm" },
				document,
				suppressRules: true);

			Assert.True(result.GetProperty("rulesSuppressed").GetBoolean());
			Assert.Equal(before, await RulesEnabledAsync(client));
			Assert.Equal(before, result.GetProperty("rulesEnabledNow").GetBoolean());
		}
		finally
		{
			await ClosePartAsync(client, document);
		}
	}

	[Fact]
	public async Task ARuleThatRunsAfterTheWritesJoinsTheStep()
	{
		LiveInventor.Require();
		await using BridgeClient client = LiveInventor.CreateClient();
		string document = await CreatePartAsync(client, 2);

		try
		{
			ExecutionResult rule = await LiveInventor.EvalAsync(client, ScriptPrelude.Apply($$"""
				Document doc = Application.Documents.Cast<Document>().First(open => open.DisplayName == "{{document}}");
				SetILogicRuleText(doc, "DoubleSize1", "Parameter(\"Size2\") = Parameter(\"Size1\") * 2" + System.Environment.NewLine);
				return "added";
				"""), Cancellation);

			Assert.True(rule.Succeeded, LiveInventor.Describe(rule));

			int before = await CommittedCountAsync(client);

			// The rule is added now, and it may have run: read the values that the test starts from.
			string start = await ValuesAsync(client, document);

			JsonElement result = await SetAsync(
				client,
				new Dictionary<string, string>(StringComparer.Ordinal) { ["Size1"] = "7 mm" },
				document,
				runRuleAfter: "DoubleSize1");

			Assert.Equal(JsonValueKind.Null, result.GetProperty("ruleError").ValueKind);
			Assert.Equal("0.7,1.4", await ValuesAsync(client, document));
			Assert.Equal(before + 1, await CommittedCountAsync(client));

			await UndoAsync(client);

			Assert.Equal(start, await ValuesAsync(client, document));
		}
		finally
		{
			await ClosePartAsync(client, document);
		}
	}

	[Fact]
	public async Task ARuleThatAParameterChangeStartsJoinsTheStep()
	{
		// A rule that names a parameter as a variable, not through Parameter("..."), runs by itself when that parameter
		// changes, inside the step. See Docs/Research/Write-Transactions-Checks.md.
		LiveInventor.Require();
		await using BridgeClient client = LiveInventor.CreateClient();
		string document = await CreatePartAsync(client, 2);

		try
		{
			ExecutionResult rule = await LiveInventor.EvalAsync(client, ScriptPrelude.Apply($$"""
				Document doc = Application.Documents.Cast<Document>().First(open => open.DisplayName == "{{document}}");
				SetILogicRuleText(doc, "FollowSize1", "Size2 = Size1 * 2" + System.Environment.NewLine);
				return "added";
				"""), Cancellation);

			Assert.True(rule.Succeeded, LiveInventor.Describe(rule));

			// Adding the rule runs it once: read the values that the test starts from.
			string start = await ValuesAsync(client, document);
			int before = await CommittedCountAsync(client);

			_ = await SetAsync(client, new Dictionary<string, string>(StringComparer.Ordinal) { ["Size1"] = "7 mm" }, document);

			Assert.Equal("0.7,1.4", await ValuesAsync(client, document));
			Assert.Equal(before + 1, await CommittedCountAsync(client));

			await UndoAsync(client);

			Assert.Equal(start, await ValuesAsync(client, document));
		}
		finally
		{
			await ClosePartAsync(client, document);
		}
	}

	[Fact]
	public async Task ARuleThatSavesInsideTheStepKeepsTheUndoStep()
	{
		// If this test fails, run runRuleAfter after the step. See Docs/Research/Write-Transactions-Checks.md.
		// The part is saved to the temporary folder. In a Vault project, the save can show a dialog: read it with
		// inventor_dialogs.
		LiveInventor.Require();
		await using BridgeClient client = LiveInventor.CreateClient();
		string document = await CreatePartAsync(client, 1);
		string? path = null;

		try
		{
			ExecutionResult saved = await LiveInventor.EvalAsync(client, ScriptPrelude.Apply($$"""
				Document doc = Application.Documents.Cast<Document>().First(open => open.DisplayName == "{{document}}");
				string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "InventorMcp-SaveInStep-" + Guid.NewGuid().ToString("N")[..8] + ".ipt");
				doc.SaveAs(path, false);
				SetILogicRuleText(doc, "SaveMe", "ThisDoc.Document.Save()" + System.Environment.NewLine);
				doc.Save();
				return new { path, name = doc.DisplayName };
				"""), Cancellation);

			Assert.True(saved.Succeeded, LiveInventor.Describe(saved));

			JsonElement json = JsonDocument.Parse(saved.ReturnValue!).RootElement;
			path = json.GetProperty("path").GetString()!;
			document = json.GetProperty("name").GetString()!;

			JsonElement result = await SetAsync(
				client,
				new Dictionary<string, string>(StringComparer.Ordinal) { ["Size1"] = "8 mm" },
				document,
				runRuleAfter: "SaveMe");

			Assert.Equal(JsonValueKind.Null, result.GetProperty("ruleError").ValueKind);
			Assert.Equal("MCP: Set Parameters", await LastTransactionNameAsync(client));

			await UndoAsync(client);

			Assert.Equal("1 mm", await ExpressionsAsync(client, document));
		}
		finally
		{
			await ClosePartAsync(client, document);

			if (path is not null && File.Exists(path))
				File.Delete(path);
		}
	}

	[Fact]
	public async Task AnUnknownParameterGivesNotFoundFromTheSingleParameterTool()
	{
		LiveInventor.Require();
		await using BridgeClient client = LiveInventor.CreateClient();
		string document = await CreatePartAsync(client, 1);

		try
		{
			JsonElement result = JsonSerializer.SerializeToElement(await InventorTool.SetParameterAsync(client, "Missing", "1 mm", document, cancellationToken: Cancellation));

			Assert.Equal("not-found", result.GetProperty("error").GetString());

			JsonElement worked = JsonSerializer.SerializeToElement(await InventorTool.SetParameterAsync(client, "Size1", "12 mm", document, cancellationToken: Cancellation));

			Assert.Equal("Size1", worked.GetProperty("name").GetString());
			Assert.Equal("Numeric", worked.GetProperty("valueKind").GetString());
			Assert.Equal("12 mm", worked.GetProperty("expression").GetString());
			Assert.Equal(1.2, worked.GetProperty("internalValue").GetDouble(), 6);
			Assert.Equal("MCP: Set Parameters", worked.GetProperty("transaction").GetString());
		}
		finally
		{
			await ClosePartAsync(client, document);
		}
	}

	private static async Task<JsonElement> SetAsync(
		BridgeClient client,
		Dictionary<string, string> writes,
		string document,
		bool suppressRules = false,
		string? runRuleAfter = null)
	{
		JsonElement result = JsonSerializer.SerializeToElement(await InventorTool.SetParametersAsync(
			client,
			writes,
			document,
			suppressRules,
			runRuleAfter,
			createIfMissing: false,
			allowUnsavedChanges: true,
			cancellationToken: Cancellation));

		Assert.False(result.TryGetProperty("error", out _), result.GetRawText());

		return result;
	}

	/// <summary>
	/// 	Creates a part with the user parameters <c>Size1</c> to <c>SizeN</c>, each "1 mm", and gives its name.
	/// </summary>
	private static async Task<string> CreatePartAsync(BridgeClient client, int parameterCount)
	{
		ExecutionResult created = await LiveInventor.EvalAsync(client, $$"""
			PartDocument part = (PartDocument)Application.Documents.Add(
				DocumentTypeEnum.kPartDocumentObject,
				Application.FileManager.GetTemplateFile(DocumentTypeEnum.kPartDocumentObject),
				true);
			for (int index = 1; index <= {{parameterCount}}; index++)
				part.ComponentDefinition.Parameters.UserParameters.AddByExpression("Size" + index, "1 mm", UnitsTypeEnum.kDefaultDisplayLengthUnits);
			return part.DisplayName;
			""", Cancellation);

		Assert.True(created.Succeeded, LiveInventor.Describe(created));

		return created.ReturnValue!;
	}

	private static async Task ClosePartAsync(BridgeClient client, string document) =>
		_ = await LiveInventor.EvalAsync(client, $$"""
			foreach (Document open in Application.Documents.Cast<Document>().Where(open => open.DisplayName == "{{document}}").ToList())
				open.Close(true);
			return "closed";
			""", Cancellation);

	private static async Task<int> CommittedCountAsync(BridgeClient client) =>
		int.Parse((await LiveInventor.EvalAsync(client, "return Application.TransactionManager.CommittedTransactions.Count;", Cancellation)).ReturnValue!, System.Globalization.CultureInfo.InvariantCulture);

	private static async Task<string> LastTransactionNameAsync(BridgeClient client) =>
		(await LiveInventor.EvalAsync(client, "return Application.TransactionManager.CommittedTransactions.Cast<Transaction>().Last().DisplayName;", Cancellation)).ReturnValue!;

	private static async Task UndoAsync(BridgeClient client) =>
		_ = await LiveInventor.EvalAsync(client, "Application.TransactionManager.UndoTransaction(); return \"undone\";", Cancellation);

	private static async Task<bool> RulesEnabledAsync(BridgeClient client) =>
		bool.Parse((await LiveInventor.EvalAsync(client, ScriptPrelude.Apply("return (bool)ILogicAutomation().RulesEnabled;"), Cancellation)).ReturnValue!);

	/// <summary>
	/// 	The expression of each user parameter, in the order of the part, joined with commas.
	/// </summary>
	private static async Task<string> ExpressionsAsync(BridgeClient client, string document) =>
		(await LiveInventor.EvalAsync(client, $$"""
			PartDocument part = (PartDocument)Application.Documents.Cast<Document>().First(open => open.DisplayName == "{{document}}");
			return string.Join(",", part.ComponentDefinition.Parameters.UserParameters.Cast<UserParameter>().Select(parameter => parameter.Expression));
			""", Cancellation)).ReturnValue!;

	/// <summary>
	/// 	The internal value (centimeters) of each user parameter, in the order of the part, joined with commas.
	/// </summary>
	private static async Task<string> ValuesAsync(BridgeClient client, string document) =>
		(await LiveInventor.EvalAsync(client, $$"""
			PartDocument part = (PartDocument)Application.Documents.Cast<Document>().First(open => open.DisplayName == "{{document}}");
			return string.Join(",", part.ComponentDefinition.Parameters.UserParameters.Cast<UserParameter>().Select(parameter => Math.Round(parameter._Value, 6).ToString(System.Globalization.CultureInfo.InvariantCulture)));
			""", Cancellation)).ReturnValue!;
}
