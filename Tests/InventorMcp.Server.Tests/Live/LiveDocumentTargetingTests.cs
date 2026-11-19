using System.Text.Json;

using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.McpTools;
using InventorMcp.Server.Tests.Desktop;

namespace InventorMcp.Server.Tests.Live;

/// <summary>
/// 	Each tool finds its document the same way, in a live Inventor: the edit document, and the model states of a file.
/// </summary>
/// <remarks>
/// 	Needs Inventor with the add-in loaded. Each test saves a part and an assembly in a new folder under the temporary
/// 	folder, closes them, and deletes the folder.
/// </remarks>
[Trait("Level", "Live")]
[Collection(DesktopCollection.Name)]
public sealed class LiveDocumentTargetingTests
{
	private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

	[Fact]
	public async Task AWriteWithNoNameDuringAnEditInPlaceChangesThePartInEdit()
	{
		LiveInventor.Require();
		await using BridgeClient client = LiveInventor.CreateClient();
		string folder = NewFolder();

		try
		{
			// The part and the assembly both have Width, so a write to the wrong document would succeed too.
			_ = await EvalAsync(client, $$"""
				string folder = {{Literal(folder)}};
				PartDocument part = (PartDocument)Application.Documents.Add(DocumentTypeEnum.kPartDocumentObject, Application.FileManager.GetTemplateFile(DocumentTypeEnum.kPartDocumentObject), true);
				part.ComponentDefinition.Parameters.UserParameters.AddByExpression("Width", "10 mm", "mm");
				part.SaveAs(System.IO.Path.Combine(folder, "Edit_Part.ipt"), false);
				part.Close(true);
				AssemblyDocument assembly = (AssemblyDocument)Application.Documents.Add(DocumentTypeEnum.kAssemblyDocumentObject, Application.FileManager.GetTemplateFile(DocumentTypeEnum.kAssemblyDocumentObject), true);
				assembly.ComponentDefinition.Parameters.UserParameters.AddByExpression("Width", "10 mm", "mm");
				assembly.ComponentDefinition.Occurrences.Add(System.IO.Path.Combine(folder, "Edit_Part.ipt"), Application.TransientGeometry.CreateMatrix());
				assembly.SaveAs(System.IO.Path.Combine(folder, "Edit_Assembly.iam"), false);
				assembly.ComponentDefinition.Occurrences[1].Edit();
				return "ready";
				""");

			JsonElement result = JsonSerializer.SerializeToElement(await InventorTool.SetParameterAsync(client, "Width", "20 mm", cancellationToken: Cancellation));
			Assert.False(result.TryGetProperty("error", out _), result.GetRawText());

			string values = await EvalAsync(client, """
				AssemblyDocument assembly = (AssemblyDocument)Application.ActiveDocument;
				PartDocument part = (PartDocument)Application.ActiveEditDocument;
				string text = $"part={part.ComponentDefinition.Parameters["Width"].Expression} assembly={assembly.ComponentDefinition.Parameters["Width"].Expression}";
				assembly.ComponentDefinition.Occurrences[1].ExitEdit(ExitTypeEnum.kExitToTop);
				return text;
				""");

			Assert.Equal("part=20 mm assembly=10 mm", values);
		}
		finally
		{
			await CloseAndDeleteAsync(client, folder);
		}
	}

	[Fact]
	public async Task APathWithTwoModelStatesOpenIsAmbiguousAndAFullDocumentNameIsNot()
	{
		LiveInventor.Require();
		await using BridgeClient client = LiveInventor.CreateClient();
		string folder = NewFolder();
		string partPath = Path.Combine(folder, "State_Part.ipt");

		try
		{
			_ = await EvalAsync(client, $$"""
				string folder = {{Literal(folder)}};
				PartDocument part = (PartDocument)Application.Documents.Add(DocumentTypeEnum.kPartDocumentObject, Application.FileManager.GetTemplateFile(DocumentTypeEnum.kPartDocumentObject), true);
				part.ComponentDefinition.ModelStates.Add("State2");
				part.SaveAs({{Literal(partPath)}}, false);
				part.Close(true);
				AssemblyDocument assembly = (AssemblyDocument)Application.Documents.Add(DocumentTypeEnum.kAssemblyDocumentObject, Application.FileManager.GetTemplateFile(DocumentTypeEnum.kAssemblyDocumentObject), true);
				assembly.ComponentDefinition.Occurrences.Add({{Literal(partPath)}}, Application.TransientGeometry.CreateMatrix());
				assembly.ComponentDefinition.Occurrences.Add({{Literal(partPath)}}, Application.TransientGeometry.CreateMatrix()).ActiveModelState = "State2";
				assembly.SaveAs(System.IO.Path.Combine(folder, "State_Assembly.iam"), false);
				return "ready";
				""");

			InventorBridgeException ambiguous = await Assert.ThrowsAsync<InventorBridgeException>(
				() => client.InvokeAsync<IReadOnlyList<ParameterInfo>>(BridgeOperations.Parameters, new ParametersRequest(partPath), Cancellation));

			Assert.Equal(BridgeErrorCodes.AmbiguousDocument, ambiguous.Code);
			Assert.Contains(partPath + "<State2>", ambiguous.Message, StringComparison.OrdinalIgnoreCase);

			// The full document name of State2 matches its factory and its member, and the factory is the one.
			_ = await client.InvokeAsync<JsonElement>(BridgeOperations.Parameters, new ParametersRequest(partPath + "<State2>"), Cancellation);

			IReadOnlyList<DocumentInfo> documents = await client.InvokeAsync<IReadOnlyList<DocumentInfo>>(BridgeOperations.Documents, null, Cancellation);
			Assert.Contains(documents, document => string.Equals(document.FullDocumentName, partPath, StringComparison.OrdinalIgnoreCase) && document.ModelStateName == "[Primary]");
			Assert.Contains(documents, document => string.Equals(document.FullDocumentName, partPath + "<State2>", StringComparison.OrdinalIgnoreCase) && document.ModelStateName == "State2");
		}
		finally
		{
			await CloseAndDeleteAsync(client, folder);
		}
	}

	private static string NewFolder()
	{
		string folder = Path.Combine(Path.GetTempPath(), LiveInventor.UniqueName("McpTargeting"));
		_ = Directory.CreateDirectory(folder);

		return folder;
	}

	private static string Literal(string value) => JsonSerializer.Serialize(value);

	private static async Task<string> EvalAsync(BridgeClient client, string code)
	{
		ExecutionResult result = await LiveInventor.EvalAsync(client, code, Cancellation);
		Assert.True(result.Succeeded, $"{result.ExceptionType}: {result.ExceptionMessage} {string.Join(" ", result.Diagnostics)}");

		return result.ReturnValue ?? string.Empty;
	}

	private static async Task CloseAndDeleteAsync(BridgeClient client, string folder)
	{
		_ = await LiveInventor.EvalAsync(client, $$"""
			foreach (Document document in Application.Documents.VisibleDocuments.OfType<Document>().ToList())
			{
				if (document.FullFileName.StartsWith({{Literal(folder)}}, StringComparison.OrdinalIgnoreCase))
					document.Close(true);
			}
			return "closed";
			""", Cancellation);

		Directory.Delete(folder, recursive: true);
	}
}
