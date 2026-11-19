using System.Runtime.InteropServices;
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
/// 	The ping and the activity feed are the exceptions: they read plain managed memory, and an instance of this class is
/// 	not given to them (see <see cref="RegisterWithoutMainThread"/>).
/// </remarks>
internal sealed partial class InventorOperations(
	Application inventor,
	MainThreadDispatcher mainThread)
{
	private readonly Application _inventor = inventor;
	private readonly MainThreadDispatcher _mainThread = mainThread;

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
		InventorOperations operations = new(inventor, mainThread);

		RegisterWithoutMainThread(dispatcher, activity);

		dispatcher.Register(BridgeOperations.Session, (_, token) =>
			operations.OnMainThreadAsync(operations.GetSession, token));

		dispatcher.Register(BridgeOperations.Documents, (_, token) =>
			operations.OnMainThreadAsync(operations.GetDocuments, token));

		dispatcher.Register(BridgeOperations.AssemblyTree, (payload, token) =>
		{
			AssemblyTreeRequest request = ParseOrDefault(payload, new AssemblyTreeRequest());
			return operations.OnMainThreadAsync(() => operations.GetAssemblyTree(request), token);
		});

		dispatcher.Register(BridgeOperations.Parameters, (payload, token) =>
		{
			ParametersRequest request = ParseOrDefault(payload, new ParametersRequest());
			return operations.OnMainThreadAsync(() => operations.GetParameters(request), token);
		});

		dispatcher.Register(BridgeOperations.EvaluateExpression, (payload, token) =>
		{
			EvaluateExpressionRequest request = ParseRequired<EvaluateExpressionRequest>(payload);
			return operations.OnMainThreadAsync(() => operations.EvaluateExpression(request), token);
		});

		dispatcher.Register(BridgeOperations.Properties, (payload, token) =>
		{
			PropertiesRequest request = ParseOrDefault(payload, new PropertiesRequest());
			return operations.OnMainThreadAsync(() => operations.GetProperties(request), token);
		});

		dispatcher.Register(BridgeOperations.SetProperty, (payload, token) =>
		{
			SetPropertyRequest request = ParseRequired<SetPropertyRequest>(payload);
			return operations.OnMainThreadAsync(() => operations.SetProperty(request), token);
		});

		dispatcher.Register(BridgeOperations.Health, (payload, token) =>
		{
			DocumentScopedRequest request = ParseOrDefault(payload, new DocumentScopedRequest());
			return operations.OnMainThreadAsync(() => operations.GetHealth(request), token);
		});

		dispatcher.Register(BridgeOperations.Update, (payload, token) =>
		{
			UpdateRequest request = ParseOrDefault(payload, new UpdateRequest());
			return operations.OnMainThreadAsync(() => operations.UpdateDocument(request), token);
		});

		dispatcher.Register(BridgeOperations.EvalCSharp, (payload, token) =>
		{
			ExecuteRequest request = ParseRequired<ExecuteRequest>(payload);
			return operations.EvaluateCSharpAsync(request, token);
		});

		dispatcher.Register(BridgeOperations.RunILogic, (payload, token) =>
		{
			ExecuteRequest request = ParseRequired<ExecuteRequest>(payload);
			return operations.OnMainThreadAsync(() => operations.RunILogicRule(request), token);
		});
	}

	/// <summary>
	/// 	Registers the operations that never use the main thread.
	/// </summary>
	/// <remarks>
	/// 	Static, and with no Inventor object or dispatcher, so these handlers cannot reach the main thread. They skip the
	/// 	queue of their connection, so a call to the main thread would run nested in the modal loop of a dialog.
	/// </remarks>
	/// <param name="dispatcher">
	/// 	The dispatcher the bridge server reads from.
	/// </param>
	/// <param name="activity">
	/// 	The recorder holding the event ring buffer, which is managed memory.
	/// </param>
	private static void RegisterWithoutMainThread(OperationDispatcher dispatcher, ActivityRecorder activity)
	{
		dispatcher.RegisterWithoutMainThread(BridgeOperations.Ping, static _ => Task.FromResult<object?>("pong"));

		dispatcher.RegisterWithoutMainThread(BridgeOperations.Activity, payload =>
		{
			ActivityRequest request = ParseOrDefault(payload, new ActivityRequest());
			return Task.FromResult<object?>(activity.Drain(request.SinceSequence, request.MaxEntries));
		});
	}

	/// <summary>
	/// 	Compiles a snippet away from Inventor's main thread, then runs it there.
	/// </summary>
	/// <remarks>
	/// 	The compile is the slow part of a new snippet, and it uses no Inventor object.
	/// 	Task.Run, so that the compile never runs on the thread that called the handler.
	/// </remarks>
	private async Task<object?> EvaluateCSharpAsync(ExecuteRequest request, CancellationToken cancellationToken)
	{
		CompiledScript compiled = await Task.Run(() => _scripts.GetOrCompile(request.Code), cancellationToken).ConfigureAwait(false);

		return await _mainThread.InvokeAsync(() => EvaluateCSharp(request, compiled), cancellationToken).ConfigureAwait(false);
	}

	private async Task<object?> OnMainThreadAsync<TResult>(Func<TResult> work, CancellationToken cancellationToken) =>
		await _mainThread.InvokeAsync(work, cancellationToken).ConfigureAwait(false);

	private static T ParseOrDefault<T>(JsonElement? payload, T fallback)
	{
		if (payload is not JsonElement element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
			return fallback;

		return element.Deserialize<T>(BridgeProtocol.SerializerOptions) ?? fallback;
	}

	private static T ParseRequired<T>(JsonElement? payload)
	{
		if (payload is not JsonElement element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
			throw new BridgeFailureException(BridgeErrorCodes.Internal, $"The operation needs a payload of type {typeof(T).Name}.");

		return element.Deserialize<T>(BridgeProtocol.SerializerOptions)
			?? throw new BridgeFailureException(BridgeErrorCodes.Internal, $"The payload could not be read as {typeof(T).Name}.");
	}

	/// <summary>
	/// 	Finds the document an operation targets. Every tool finds its document here.
	/// </summary>
	/// <remarks>
	/// 	With no name, the document in edit: during an edit in place of a part in an assembly, that is the part.
	/// 	With no edit in place, <c>ActiveEditDocument</c> is the active document. See <see cref="DocumentNameMatch"/> for a
	/// 	name.
	/// </remarks>
	/// <param name="documentName">
	/// 	A full document name, a path or a display name. Null selects the document in edit.
	/// </param>
	/// <returns>
	/// 	The matching document.
	/// </returns>
	/// <exception cref="BridgeFailureException">
	/// 	No document is active (<see cref="BridgeErrorCodes.NoActiveDocument"/>), no open document matches
	/// 	(<see cref="BridgeErrorCodes.NotFound"/>), or more than one does (<see cref="BridgeErrorCodes.AmbiguousDocument"/>).
	/// </exception>
	private Document ResolveDocument(string? documentName)
	{
		if (string.IsNullOrWhiteSpace(documentName))
		{
			return (_inventor.ActiveEditDocument ?? _inventor.ActiveDocument) as Document
				?? throw new BridgeFailureException(BridgeErrorCodes.NoActiveDocument, "Inventor has no active document.");
		}

		Documents documents = _inventor.Documents;
		List<Document> open = new(documents.Count);

		for (int index = 1; index <= documents.Count; index++)
			open.Add((Document)documents[index]);

		// COM gives null, not an empty string, for the path and the full document name of a document that was never saved.
		DocumentMatch match = DocumentNameMatch.Find(
			[.. open.Select(static document => new DocumentNames(
				document.DisplayName ?? string.Empty,
				document.FullFileName ?? string.Empty,
				document.FullDocumentName ?? string.Empty,
				IsModelStateFactory(document)))],
			documentName);

		if (match.Index is int found)
			return open[found];

		if (match.IsAmbiguous)
		{
			throw new BridgeFailureException(
				BridgeErrorCodes.AmbiguousDocument,
				$"'{documentName}' matches {match.Candidates.Count} open documents. Call again with one of these full document names: {string.Join("; ", match.Candidates)}.",
				string.Join(System.Environment.NewLine, match.Candidates));
		}

		throw new BridgeFailureException(
			BridgeErrorCodes.NotFound,
			$"No open document matches '{documentName}'. Call the documents operation to list what is open.");
	}

	/// <summary>
	/// 	Tells if a document is the model state factory of its file.
	/// </summary>
	private static bool IsModelStateFactory(Document document) => document switch
	{
		PartDocument part => part.ComponentDefinition.IsModelStateFactory,
		AssemblyDocument assembly => assembly.ComponentDefinition.IsModelStateFactory,
		_ => false
	};

	private static DocumentInfo ToDocumentInfo(Document document) => new(
		document.DisplayName,
		document.FullFileName,
		DescribeDocumentType(document.DocumentType),
		document.Dirty,
		document.IsModifiable,
		document.RequiresUpdate,
		document.FullDocumentName,
		ModelStateName(document));

	/// <summary>
	/// 	The model state of a document, or null for a document that has none (ex. a drawing).
	/// </summary>
	private static string? ModelStateName(Document document)
	{
		try
		{
			return document.ModelStateName is { Length: > 0 } name ? name : null;
		}
		catch (COMException)
		{
			// A document type with no model states throws, and has none to report.
			return null;
		}
	}

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
