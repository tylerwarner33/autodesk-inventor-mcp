using System.ComponentModel;
using System.Text.Json;

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

		Before your first snippet, read the 'interop' skill: inventor_skill with action 'read' and name 'interop'. It
		lists the API traps that make most snippets fail, ex. how to tell a text or boolean parameter from a numeric one.

		The snippet is Roslyn script code, not a full class. 'Application' and 'Document' are already in scope, and
		System, System.Collections.Generic, System.Linq and Inventor are imported. Call Log(...) to report progress,
		and end with an expression or a return statement to return a value. A list, dictionary, anonymous object, record
		or tuple of plain values comes back as JSON. Anything else, ex. an Inventor object, comes back as its ToString,
		which is often only its type name, so return the values you need.

		'Document' is null when no document is open and none is named. The snippet still runs, so it can open or
		create its own documents, ex. Application.Documents.Add, or run a plugin that does.

		Lengths are in centimetres and angles in radians, because those are Inventor's internal units. Convert with
		Document.UnitsOfMeasure.ConvertUnits or write expressions through parameters instead.

		Keep each call under 10 s. The snippet runs on Inventor's main thread, so Inventor processes no window messages
		until it returns. A long loop that creates or edits sketches, views or documents can fill the message queue and
		terminate Inventor. Split such a loop over several calls. A result from a call over 10 s carries a warning.
		wallClockMilliseconds minus elapsedMilliseconds is the time the call waited behind other work, or to open a
		document.

		This executes arbitrary code in the user's CAD session. Every snippet is written to an audit log. Execution is
		refused when the document has unsaved changes unless allowUnsavedChanges is set, because the work cannot be
		recovered. Opening or updating a generated drawing marks it as changed, so a later call on it can be refused:
		pass allowUnsavedChanges when the only changes are from that open. Prefer inventor_set_parameter for a change
		a parameter can express.

		A compile error for a member that does not exist carries a HINT with the near members of that type. Some members
		are typed as object and need a cast, ex. (double)parameter.Value. Calls from all clients run one at a time, so a
		slow call can be waiting behind other work.

		Helpers you can call:
		- OpenOrReuseDocument(fullFileName, visible = false) returns the open copy or opens it, and
		  CloseDocumentsOpenedHere() closes only the documents it opened, drawings first, with no save.
		- ILogicAutomation(), ILogicRuleNames(document), ILogicRuleText(document, name),
		  SetILogicRuleText(document, name, text) and RunILogicRule(document, name) take any document type.
		- ToInches, FromInches, ToMillimetres, FromMillimetres (from and to centimetres), ToDegrees, FromDegrees.
		- TryGetUserParameter(document, name, out UserParameter parameter).
		- StartDeadline(seconds = 8) returns a guard for a loop: if (deadline.Passed) break;
		""")]
	public static Task<object> EvaluateCSharp(
		BridgeClient bridge,
		ApiReferenceService apiReference,
		[Description("The C# snippet to run.")] string code,
		[Description("Display name or full path of the document. Omit to use the active document.")] string? documentName = null,
		[Description("Set true to run against a document with unsaved changes. Those changes cannot be recovered if the snippet damages them.")] bool allowUnsavedChanges = false,
		[Description("Turn iLogic rules off while the snippet runs, so a parameter change does not run them. They are turned back on after it, also after a failure. RulesEnabled is a setting of the whole session.")] bool suppressRules = false,
		CancellationToken cancellationToken = default) =>
		SafeAsync(() => WithRulesSuppressedAsync(
			bridge,
			suppressRules,
			async () => await AddFailureContextAsync(
				bridge,
				DiagnosticHints.Improve(
					await bridge.InvokeAsync<Contracts.Models.ExecutionResult>(
						BridgeOperations.EvalCSharp,
						new ExecuteRequest(ScriptPrelude.Apply(code), documentName, allowUnsavedChanges),
						cancellationToken).ConfigureAwait(false),
					apiReference,
					bridge.EffectiveReleaseYear),
				documentName,
				cancellationToken).ConfigureAwait(false),
			cancellationToken));

	/// <summary>
	/// 	Adds the state of the target document and the project to a result that failed with only E_FAIL.
	/// </summary>
	/// <remarks>
	/// 	E_FAIL gives no cause, and the common one is a document that is not modifiable, ex. in a library path.
	/// 	One more bridge call, only after such a failure.
	/// </remarks>
	private static async Task<Contracts.Models.ExecutionResult> AddFailureContextAsync(
		BridgeClient bridge,
		Contracts.Models.ExecutionResult result,
		string? documentName,
		CancellationToken cancellationToken)
	{
		if (DiagnosticHints.IsUnspecifiedComFailure(result) is false)
			return result;

		JsonElement state;

		try
		{
			state = await ReadSessionStateAsync(bridge, documentName, cancellationToken).ConfigureAwait(false);
		}
		catch (InventorBridgeException)
		{
			return result;
		}

		if (state.TryGetProperty("project", out JsonElement project) is false)
			return result;

		string target = "no document";

		if (state.GetProperty("documents").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } document)
		{
			target = $"'{document.GetProperty("name").GetString()}' isModifiable={document.GetProperty("isModifiable").GetBoolean()}, " +
				$"readOnlyFile={document.GetProperty("readOnlyFile").GetBoolean()}, " +
				$"library={(document.GetProperty("library").GetString() ?? "none")}";
		}

		string context = $"CONTEXT: this COM error gives no cause. The target document: {target}. " +
			$"Project '{project.GetProperty("file").GetString()}', workspace '{project.GetProperty("workspace").GetString()}'. " +
			"A write to a document that is not modifiable (ex. in a library path, read-only, or checked in to Vault) fails with E_FAIL. " +
			"Other causes: an object that is no longer valid, or an argument that the member does not accept.";

		return result with { Output = [.. result.Output, context] };
	}

	/// <summary>
	/// 	Runs a call with iLogic rules turned off, and turns them back on after it.
	/// </summary>
	/// <remarks>
	/// 	Three bridge calls, not one snippet with a finally block: the model's snippet can declare methods and return at
	/// 	the top level, which a wrapping try block would break. The restore runs in the server's finally block, so a
	/// 	failed snippet still restores. If the restore cannot run (ex. a dialog blocks Inventor), the result says so.
	/// </remarks>
	private static async Task<object> WithRulesSuppressedAsync<TResult>(
		BridgeClient bridge,
		bool suppressRules,
		Func<Task<TResult>> work,
		CancellationToken cancellationToken)
	{
		if (suppressRules is false)
			return (await work().ConfigureAwait(false))!;

		JsonElement before = await RunJsonSnippetAsync(bridge, """
			dynamic automation = ILogicAutomation();
			bool previous = (bool)automation.RulesEnabled;
			automation.RulesEnabled = false;
			return System.Text.Json.JsonSerializer.Serialize(new { previous });
			""", cancellationToken).ConfigureAwait(false);

		if (before.TryGetProperty("previous", out JsonElement previousElement) is false)
			return new { error = BridgeErrorCodes.ExecutionFailed, message = "Could not turn the iLogic rules off, so the snippet did not run.", detail = before };

		bool previous = previousElement.GetBoolean();
		object? result = null;
		string? restoreProblem = null;

		try
		{
			result = await work().ConfigureAwait(false);
		}
		finally
		{
			try
			{
				_ = await RunJsonSnippetAsync(
					bridge,
					$"ILogicAutomation().RulesEnabled = {(previous ? "true" : "false")}; return \"{{}}\";",
					CancellationToken.None).ConfigureAwait(false);
			}
			catch (InventorBridgeException exception)
			{
				restoreProblem = $"iLogic rules are still turned off, because the restore failed: {exception.Message} " +
					"Tell the user. Call inventor_eval_csharp with 'ILogicAutomation().RulesEnabled = true; return 0;' when Inventor answers.";
			}
		}

		return restoreProblem is null
			? new { result, rulesSuppressed = true, rulesEnabledNow = previous }
			: new { result, rulesSuppressed = true, warning = restoreProblem };
	}

	[McpServerTool(Name = "inventor_run_ilogic")]
	[Description("""
		Runs an iLogic rule body, written in VB.NET, against a document. The rule is added temporarily, run, and removed,
		so nothing is left behind in the document.

		Use this when the work is naturally an iLogic rule, ex. driving parameters and using iLogic helpers such as
		Parameter, iProperties or Component. Use inventor_eval_csharp when you need the raw Inventor API.

		This executes arbitrary code in the user's CAD session and is audited and guarded the same way as
		inventor_eval_csharp. Keep each call under 10 s.

		Give ruleName instead of code to run a rule that the document already has. Nothing can stop a rule that runs
		on the main thread, so a long rule can only be reported after it returns.
		""")]
	public static Task<object> RunILogic(
		BridgeClient bridge,
		[Description("The iLogic rule body, in VB.NET. Omit when ruleName is given.")] string? code = null,
		[Description("Display name or full path of an open document. Omit to use the active document.")] string? documentName = null,
		[Description("Set true to run against a document with unsaved changes. Those changes cannot be recovered if the rule damages them.")] bool allowUnsavedChanges = false,
		[Description("The name of a rule in the document to run, in place of code.")] string? ruleName = null,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(code) == string.IsNullOrWhiteSpace(ruleName))
			return Task.FromResult<object>(new { error = "invalid-arguments", message = "Give code to run a rule body, or ruleName to run a rule of the document. Not both." });

		if (ruleName is null)
		{
			return SafeAsync(() => bridge.InvokeAsync<Contracts.Models.ExecutionResult>(
				BridgeOperations.RunILogic,
				new ExecuteRequest(code!, documentName, allowUnsavedChanges),
				cancellationToken));
		}

		return SafeAsync(async () => await RunJsonSnippetAsync(bridge, _findToolDocument + $$"""
			Document document = FindToolDocument({{CSharpLiteral.String(documentName)}}, allowOpen: false);
			string ruleName = {{CSharpLiteral.String(ruleName)}};
			if (!{{(allowUnsavedChanges ? "true" : "false")}} && document.Dirty)
				throw new InvalidOperationException($"'{document.DisplayName}' has unsaved changes. Save it, or pass allowUnsavedChanges.");
			if (ILogicRuleText(document, ruleName) is null)
				throw new ArgumentException($"'{document.DisplayName}' has no rule named '{ruleName}'. Call inventor_ilogic_rules for the names.");
			System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
			RunILogicRule(document, ruleName);
			return System.Text.Json.JsonSerializer.Serialize(new { document = document.DisplayName, rule = ruleName, ruleMilliseconds = stopwatch.ElapsedMilliseconds, dirty = document.Dirty });
			""", cancellationToken).ConfigureAwait(false));
	}

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
		apiReference.Search(query, kind, maxResults <= 0 ? 20 : maxResults, inventorRelease ?? bridge.EffectiveReleaseYear);
}
