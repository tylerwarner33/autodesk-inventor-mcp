using System.ComponentModel;

using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

internal static partial class InventorTool
{
	[McpServerTool(Name = "inventor_eval_csharp")]
	[Description("""
		Runs a C# snippet inside the Inventor process against the live API. This is the general purpose tool: it reaches
		every member of the Inventor API, so use it for anything the other tools do not cover, such as creating sketches,
		features, or geometry.

		The snippet is Roslyn script code, not a full class. 'Application' and 'Document' are already in scope, and
		System, System.Collections.Generic, System.Linq and Inventor are imported. Call Log(...) to report progress,
		and end with an expression or a return statement to return a value.

		'Document' is null when no document is open and none is named. The snippet still runs, so it can open or
		create its own documents, ex. Application.Documents.Add, or run a plugin that does.

		Lengths are in centimetres and angles in radians, because those are Inventor's internal units. Convert with
		Document.UnitsOfMeasure.ConvertUnits or write expressions through parameters instead.

		Keep each call short, ex. under 10 s. The snippet runs on Inventor's main thread, so Inventor processes no window
		messages until it returns. A long loop that creates or edits sketches, views or documents can fill the message
		queue and terminate Inventor. Split such a loop over several calls. A result from a call over 20 s carries a
		warning.

		This executes arbitrary code in the user's CAD session. Every snippet is written to an audit log. Execution is
		refused when the document has unsaved changes unless allowUnsavedChanges is set, because the work cannot be
		recovered. Prefer inventor_set_parameter for a change a parameter can express.
		""")]
	public static Task<object> EvaluateCSharp(
		BridgeClient bridge,
		[Description("The C# snippet to run.")] string code,
		[Description("Display name or full path of the document. Omit to use the active document.")] string? documentName = null,
		[Description("Set true to run against a document with unsaved changes. Those changes cannot be recovered if the snippet damages them.")] bool allowUnsavedChanges = false,
		CancellationToken cancellationToken = default) =>
		SafeAsync(() => bridge.InvokeAsync<Contracts.Models.ExecutionResult>(
			BridgeOperations.EvalCSharp,
			new ExecuteRequest(code, documentName, allowUnsavedChanges),
			cancellationToken));

	[McpServerTool(Name = "inventor_run_ilogic")]
	[Description("""
		Runs an iLogic rule body, written in VB.NET, against a document. The rule is added temporarily, run, and removed,
		so nothing is left behind in the document.

		Use this when the work is naturally an iLogic rule, ex. driving parameters and using iLogic helpers such as
		Parameter, iProperties or Component. Use inventor_eval_csharp when you need the raw Inventor API.

		This executes arbitrary code in the user's CAD session and is audited and guarded the same way as
		inventor_eval_csharp.
		""")]
	public static Task<object> RunILogic(
		BridgeClient bridge,
		[Description("The iLogic rule body, in VB.NET.")] string code,
		[Description("Display name or full path of the document. Omit to use the active document.")] string? documentName = null,
		[Description("Set true to run against a document with unsaved changes. Those changes cannot be recovered if the rule damages them.")] bool allowUnsavedChanges = false,
		CancellationToken cancellationToken = default) =>
		SafeAsync(() => bridge.InvokeAsync<Contracts.Models.ExecutionResult>(
			BridgeOperations.RunILogic,
			new ExecuteRequest(code, documentName, allowUnsavedChanges),
			cancellationToken));

	[McpServerTool(Name = "inventor_api_lookup")]
	[Description("""
		Searches Autodesk's own documentation for the Inventor API: types, methods, properties, events and fields, with
		their summaries. Use it before writing a snippet for inventor_eval_csharp, to confirm a member exists and what it
		is called.

		Query by type name, member name, or 'Type.Member', ex. 'ExtrudeFeatures', 'AddByDistanceExtent', or
		'HoleFeatures.AddDrilledByThroughAllExtent'. Reads a local file, so it works with Inventor closed.

		Answers for the connected Inventor release once inventor_session has been called, and for the newest
		release shipped before that. Pass inventorRelease to ask about a different one.
		""")]
	public static IReadOnlyList<ApiMember> ApiLookup(
		ApiReferenceService apiReference,
		BridgeClient bridge,
		[Description("Type name, member name, or 'Type.Member'.")] string query,
		[Description("Restrict to one of: type, method, property, event, field. Omit for all kinds.")] string? kind = null,
		[Description("Maximum members to return. Default 20.")] int maxResults = 20,
		[Description("Inventor release to answer for, ex. 2025. Omit to follow the connected session.")] int? inventorRelease = null) =>
		apiReference.Search(query, kind, maxResults <= 0 ? 20 : maxResults, inventorRelease ?? bridge.ReleaseYear);
}
