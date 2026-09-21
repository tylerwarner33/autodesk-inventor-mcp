using System.Text.Json;

using Inventor;

using InventorMcp.AddIn.Bridge;
using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;

namespace InventorMcp.AddIn.Operations;

/// <summary>
/// 	Serves the bridge operations against the live Inventor session.
/// </summary>
/// <remarks>
/// 	Every handler that touches the Inventor API runs through <see cref="MainThreadDispatcher"/>,
/// 	because Inventor's COM objects belong to its main single threaded apartment.
/// 	Draining the activity feed is the one exception: it reads plain managed memory.
/// </remarks>
internal sealed partial class InventorOperations(
	Application inventor,
	MainThreadDispatcher mainThread,
	ActivityRecorder activity)
{
	private readonly Application _inventor = inventor;
	private readonly MainThreadDispatcher _mainThread = mainThread;
	private readonly ActivityRecorder _activity = activity;

	/// <summary>
	/// 	Registers every handler on the dispatcher.
	/// </summary>
	/// <param name="dispatcher">
	/// 	The dispatcher the bridge server reads from.
	/// </param>
	/// <param name="inventor">
	/// 	The running Inventor application.
	/// </param>
	/// <param name="mainThread">
	/// 	Marshals work onto Inventor's main thread.
	/// </param>
	/// <param name="activity">
	/// 	The recorder holding the event ring buffer.
	/// </param>
	public static void Register(
		OperationDispatcher dispatcher,
		Application inventor,
		MainThreadDispatcher mainThread,
		ActivityRecorder activity)
	{
		InventorOperations operations = new(inventor, mainThread, activity);

		dispatcher.Register(BridgeOperations.Ping, (_, _) => Task.FromResult<object?>("pong"));

		dispatcher.Register(BridgeOperations.Session, (_, token) =>
			operations.OnMainThread(operations.GetSession, token));

		dispatcher.Register(BridgeOperations.Documents, (_, token) =>
			operations.OnMainThread(operations.GetDocuments, token));

		dispatcher.Register(BridgeOperations.AssemblyTree, (payload, token) =>
		{
			AssemblyTreeRequest request = ParseOrDefault(payload, new AssemblyTreeRequest());
			return operations.OnMainThread(() => operations.GetAssemblyTree(request), token);
		});

		dispatcher.Register(BridgeOperations.Parameters, (payload, token) =>
		{
			ParametersRequest request = ParseOrDefault(payload, new ParametersRequest());
			return operations.OnMainThread(() => operations.GetParameters(request), token);
		});

		dispatcher.Register(BridgeOperations.SetParameter, (payload, token) =>
		{
			SetParameterRequest request = ParseRequired<SetParameterRequest>(payload);
			return operations.OnMainThread(() => operations.SetParameter(request), token);
		});

		dispatcher.Register(BridgeOperations.EvaluateExpression, (payload, token) =>
		{
			EvaluateExpressionRequest request = ParseRequired<EvaluateExpressionRequest>(payload);
			return operations.OnMainThread(() => operations.EvaluateExpression(request), token);
		});

		dispatcher.Register(BridgeOperations.Properties, (payload, token) =>
		{
			PropertiesRequest request = ParseOrDefault(payload, new PropertiesRequest());
			return operations.OnMainThread(() => operations.GetProperties(request), token);
		});

		dispatcher.Register(BridgeOperations.SetProperty, (payload, token) =>
		{
			SetPropertyRequest request = ParseRequired<SetPropertyRequest>(payload);
			return operations.OnMainThread(() => operations.SetProperty(request), token);
		});

		dispatcher.Register(BridgeOperations.Health, (payload, token) =>
		{
			DocumentScopedRequest request = ParseOrDefault(payload, new DocumentScopedRequest());
			return operations.OnMainThread(() => operations.GetHealth(request), token);
		});

		dispatcher.Register(BridgeOperations.Update, (payload, token) =>
		{
			UpdateRequest request = ParseOrDefault(payload, new UpdateRequest());
			return operations.OnMainThread(() => operations.UpdateDocument(request), token);
		});

		dispatcher.Register(BridgeOperations.EvalCSharp, (payload, token) =>
		{
			ExecuteRequest request = ParseRequired<ExecuteRequest>(payload);
			return operations.OnMainThread(() => operations.EvaluateCSharp(request), token);
		});

		dispatcher.Register(BridgeOperations.RunILogic, (payload, token) =>
		{
			ExecuteRequest request = ParseRequired<ExecuteRequest>(payload);
			return operations.OnMainThread(() => operations.RunILogicRule(request), token);
		});

		// The ring buffer is managed memory, so this stays responsive while Inventor is busy.
		dispatcher.Register(BridgeOperations.Activity, (payload, _) =>
		{
			ActivityRequest request = ParseOrDefault(payload, new ActivityRequest());
			return Task.FromResult<object?>(activity.Drain(request.SinceSequence, request.MaxEntries));
		});
	}

	private async Task<object?> OnMainThread<TResult>(Func<TResult> work, CancellationToken cancellationToken) =>
		await _mainThread.InvokeAsync(work, cancellationToken).ConfigureAwait(false);

	private static T ParseOrDefault<T>(JsonElement? payload, T fallback)
	{
		if (payload is not JsonElement element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
			return fallback;

		return JsonSerializer.Deserialize<T>(element, BridgeProtocol.SerializerOptions) ?? fallback;
	}

	private static T ParseRequired<T>(JsonElement? payload)
	{
		if (payload is not JsonElement element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
			throw new BridgeFailureException(BridgeErrorCodes.Internal, $"The operation needs a payload of type {typeof(T).Name}.");

		return JsonSerializer.Deserialize<T>(element, BridgeProtocol.SerializerOptions)
			?? throw new BridgeFailureException(BridgeErrorCodes.Internal, $"The payload could not be read as {typeof(T).Name}.");
	}

	/// <summary>
	/// 	Finds the document an operation targets.
	/// </summary>
	/// <param name="documentName">
	/// 	Display name or full path. Null selects the active document.
	/// </param>
	/// <returns>
	/// 	The matching document.
	/// </returns>
	private Document ResolveDocument(string? documentName)
	{
		if (string.IsNullOrWhiteSpace(documentName))
		{
			return _inventor.ActiveDocument as Document
				?? throw new BridgeFailureException(BridgeErrorCodes.NoActiveDocument, "Inventor has no active document.");
		}

		Documents documents = _inventor.Documents;

		for (int index = 1; index <= documents.Count; index++)
		{
			Document candidate = (Document)documents[index];

			if (string.Equals(candidate.DisplayName, documentName, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(candidate.FullFileName, documentName, StringComparison.OrdinalIgnoreCase))
			{
				return candidate;
			}
		}

		throw new BridgeFailureException(
			BridgeErrorCodes.NotFound,
			$"No open document matches '{documentName}'. Call the documents operation to list what is open.");
	}

	private static DocumentInfo ToDocumentInfo(Document document) => new(
		document.DisplayName,
		document.FullFileName,
		DescribeDocumentType(document.DocumentType),
		document.Dirty,
		document.IsModifiable,
		document.RequiresUpdate);

	private static string DescribeDocumentType(DocumentTypeEnum documentType) => documentType switch
	{
		DocumentTypeEnum.kPartDocumentObject => "PartDocument",
		DocumentTypeEnum.kAssemblyDocumentObject => "AssemblyDocument",
		DocumentTypeEnum.kDrawingDocumentObject => "DrawingDocument",
		DocumentTypeEnum.kPresentationDocumentObject => "PresentationDocument",
		DocumentTypeEnum.kDesignElementDocumentObject => "DesignElementDocument",
		DocumentTypeEnum.kForeignModelDocumentObject => "ForeignModelDocument",
		DocumentTypeEnum.kSATFileDocumentObject => "SatFileDocument",
		DocumentTypeEnum.kNoDocument => "None",
		_ => documentType.ToString()
	};

	/// <summary>
	/// 	Returns the parameters collection for a document, whatever its type.
	/// </summary>
	/// <param name="document">
	/// 	The document to read.
	/// </param>
	/// <returns>
	/// 	The parameters collection.
	/// </returns>
	private static Parameters GetParametersCollection(Document document) => document switch
	{
		PartDocument part => part.ComponentDefinition.Parameters,
		AssemblyDocument assembly => assembly.ComponentDefinition.Parameters,
		_ => throw new BridgeFailureException(
			BridgeErrorCodes.WrongDocumentType,
			$"A {DescribeDocumentType(document.DocumentType)} has no parameters collection. Parameters exist on parts and assemblies.")
	};
}
