using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Inventor;

using InventorMcp.AddIn.Bridge;
using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;

namespace InventorMcp.AddIn.Operations;

internal sealed partial class InventorOperations
{
	/// <summary>
	/// 	Namespaces every snippet gets without asking.
	/// </summary>
	private static readonly string[] _scriptImports =
	[
		"System",
		"System.Collections.Generic",
		"System.Linq",
		"Inventor"
	];

	// Roslyn is loaded on first use only. A read-only session never pays for it inside Inventor's process.
	private static ScriptOptions? _scriptOptions;

	private ExecutionResult EvaluateCSharp(ExecuteRequest request)
	{
		// A snippet may need no document at all, ex. one that runs a plugin which opens its own.
		// A named document that cannot be found is still an error.
		Document? document = string.IsNullOrWhiteSpace(request.DocumentName)
			? _inventor.ActiveDocument as Document
			: ResolveDocument(request.DocumentName);

		if (document is not null)
			GuardUnsavedChanges(document, request.AllowUnsavedChanges);

		WriteAuditEntry("csharp", document, request.Code, request.ClientName);

		HashSet<string> openBefore = OpenDocumentKeys();
		ExecutionResult result = RunScript(request, document);

		if (result.Succeeded is false && result.ExceptionType != "CompilationError" && result.ExceptionType != nameof(CompilationErrorException))
		{
			List<string> leftOpen = [.. OpenDocumentKeys().Except(openBefore, StringComparer.OrdinalIgnoreCase)];

			if (leftOpen.Count > 0)
				result = result with { DocumentsLeftOpen = leftOpen };
		}

		WriteAuditResult(result);

		return result;
	}

	/// <summary>
	/// 	The full path of each open document, or the display name of one that was never saved.
	/// </summary>
	private HashSet<string> OpenDocumentKeys()
	{
		HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);

		try
		{
			foreach (Document open in _inventor.Documents)
				_ = keys.Add(string.IsNullOrEmpty(open.FullFileName) ? open.DisplayName : open.FullFileName);
		}
		catch (COMException exception)
		{
			// The list is only for the report of a failure, so a failure to read it must not change the result.
			BridgeLog.Write($"Could not list the open documents. {exception.Message}");
		}

		return keys;
	}

	private ExecutionResult RunScript(ExecuteRequest request, Document? document)
	{
		InventorScriptGlobals globals = new() { Application = _inventor, Document = document };
		Stopwatch stopwatch = Stopwatch.StartNew();

		try
		{
			// Roslyn's scripting host owns its own assembly loader and, left alone, loads a second copy of this
			// add-in from the same path into its own context. The globals object then fails to cast, because the
			// two InventorScriptGlobals types have different identities.
			// Registering the already loaded assemblies makes Roslyn reuse them instead, but only as a fallback:
			// Roslyn asks the default context first, so this add-in must never be loaded there.
			// EnterContextualReflection does not help here: the scripting host does not consult it.
			using InteractiveAssemblyLoader assemblyLoader = new();
			assemblyLoader.RegisterDependency(typeof(InventorScriptGlobals).Assembly);
			assemblyLoader.RegisterDependency(typeof(Application).Assembly);

			using (SilentOperationScope.Enter(_inventor))
			{
				Script<object> script = CSharpScript.Create<object>(
					request.Code,
					GetScriptOptions(),
					typeof(InventorScriptGlobals),
					assemblyLoader);

				ImmutableArray<Diagnostic> diagnostics = script.Compile();

				List<string> messages = [.. diagnostics
					.Where(static diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
					.Select(static diagnostic => diagnostic.ToString())];

				if (diagnostics.Any(static diagnostic => diagnostic.Severity is DiagnosticSeverity.Error))
				{
					return new ExecutionResult(
						false, null, null, globals.Output, messages, "CompilationError",
						"The snippet did not compile.", stopwatch.ElapsedMilliseconds);
				}

				// The snippet body is synchronous, so the returned task completes inline and this cannot deadlock.
				ScriptState<object> state = script.RunAsync(globals).GetAwaiter().GetResult();

				return new ExecutionResult(
					true,
					ReturnValueText.Format(state.ReturnValue),
					state.ReturnValue?.GetType().FullName,
					globals.Output,
					messages,
					null,
					null,
					stopwatch.ElapsedMilliseconds);
			}
		}
		catch (CompilationErrorException exception)
		{
			return new ExecutionResult(
				false, null, null, globals.Output,
				[.. exception.Diagnostics.Select(static diagnostic => diagnostic.ToString())],
				nameof(CompilationErrorException), exception.Message, stopwatch.ElapsedMilliseconds);
		}
		catch (Exception exception)
		{
			// The snippet threw. That is a result, not a bridge failure, so it is reported rather than raised.
			Exception reported = exception is AggregateException aggregate && aggregate.InnerExceptions.Count is 1
				? aggregate.InnerExceptions[0]
				: exception;

			return new ExecutionResult(
				false, null, null, globals.Output, [],
				reported.GetType().FullName, reported.Message, stopwatch.ElapsedMilliseconds);
		}
	}

	private ExecutionResult RunILogicRule(ExecuteRequest request)
	{
		Document document = ResolveDocument(request.DocumentName);

		GuardUnsavedChanges(document, request.AllowUnsavedChanges);
		WriteAuditEntry("ilogic", document, request.Code, request.ClientName);

		ExecutionResult result = RunTemporaryRule(request, document);
		WriteAuditResult(result);

		return result;
	}

	private ExecutionResult RunTemporaryRule(ExecuteRequest request, Document document)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();

		// The iLogic add-in exposes its automation object under this fixed identifier.
		const string iLogicAddInId = "{3BDD8D79-2179-4B11-8A5A-257B1C0263AC}";

		object automation;

		try
		{
			ApplicationAddIn iLogicAddIn = _inventor.ApplicationAddIns.ItemById[iLogicAddInId];

			if (iLogicAddIn.Activated is false)
				iLogicAddIn.Activate();

			automation = iLogicAddIn.Automation;
		}
		catch (Exception exception)
		{
			throw new BridgeFailureException(
				BridgeErrorCodes.ExecutionFailed,
				"The iLogic add-in is not available in this Inventor session.",
				exception.Message);
		}

		// A temporary rule is added, run, and removed. iLogic has no way to run rule text directly.
		string ruleName = $"InventorMcp_{Guid.NewGuid():N}";
		dynamic iLogic = automation;

		try
		{
			using (SilentOperationScope.Enter(_inventor))
			{
				dynamic rule = iLogic.AddRule(document, ruleName, string.Empty);
				rule.Text = request.Code;

				iLogic.RunRule(document, ruleName);
			}

			return new ExecutionResult(
				true, null, null, [], [], null, null, stopwatch.ElapsedMilliseconds);
		}
		catch (Exception exception)
		{
			return new ExecutionResult(
				false, null, null, [], [],
				exception.GetType().FullName, exception.Message, stopwatch.ElapsedMilliseconds);
		}
		finally
		{
			try
			{
				iLogic.DeleteRule(document, ruleName);
			}
			catch (Exception exception)
			{
				// A leftover rule is visible to the user, so it is worth recording.
				BridgeLog.Write($"Could not remove the temporary iLogic rule '{ruleName}'. {exception.Message}");
			}
		}
	}

	private static ScriptOptions GetScriptOptions()
	{
		// Built once. Resolving metadata references is slow and the set never changes.
		return _scriptOptions ??= ScriptOptions.Default
			.AddReferences(
				typeof(Application).Assembly,
				typeof(InventorScriptGlobals).Assembly)
			.AddImports(_scriptImports);
	}

	/// <summary>
	/// 	Refuses to run code against a document holding unsaved changes.
	/// </summary>
	/// <remarks>
	/// 	Executed code can change a model in ways no undo step describes.
	/// 	Work that has never been saved cannot be recovered, so the caller must say explicitly that it accepts that.
	/// </remarks>
	/// <param name="document">
	/// 	The target document.
	/// </param>
	/// <param name="allowUnsavedChanges">
	/// 	True when the caller has accepted the risk.
	/// </param>
	private static void GuardUnsavedChanges(Document document, bool allowUnsavedChanges)
	{
		if (allowUnsavedChanges || document.Dirty is false)
			return;

		throw new BridgeFailureException(
			BridgeErrorCodes.UnsavedChanges,
			$"'{document.DisplayName}' has unsaved changes. Save it first, or pass allowUnsavedChanges to accept that the changes cannot be recovered.");
	}

	/// <summary>
	/// 	Appends an executed snippet to the audit trail.
	/// </summary>
	/// <remarks>
	/// 	Arbitrary execution inside a CAD process needs a record of what ran, independent of the caller.
	/// </remarks>
	/// <param name="language">
	/// 	"csharp" or "ilogic".
	/// </param>
	/// <param name="document">
	/// 	The document the snippet targeted, or null when none was open.
	/// </param>
	/// <param name="code">
	/// 	The snippet, recorded in full.
	/// </param>
	/// <param name="clientName">
	/// 	The MCP client that sent it, or null.
	/// </param>
	private static void WriteAuditEntry(string language, Document? document, string code, string? clientName)
	{
		// Fully qualified: Inventor declares its own Environment, File and Path types, which shadow the BCL ones.
		string newLine = System.Environment.NewLine;

		// Written before the run, so the log holds the snippet even when the run takes Inventor down.
		AppendAudit(
			$"{new string('=', 80)}{newLine}" +
			$"{DateTimeOffset.UtcNow:O}  {language}  document='{document?.DisplayName ?? "(none)"}'  client='{clientName ?? "(unknown)"}'{newLine}" +
			$"{new string('-', 80)}{newLine}" +
			$"{code}{newLine}");
	}

	/// <summary>
	/// 	Appends the outcome of the snippet that the last entry recorded.
	/// </summary>
	private static void WriteAuditResult(ExecutionResult result)
	{
		string outcome = result.Succeeded ? "succeeded" : $"failed  {result.ExceptionType}: {result.ExceptionMessage}";
		string leftOpen = result.DocumentsLeftOpen is { Count: > 0 } documents ? $"  left open: {string.Join(", ", documents)}" : string.Empty;

		AppendAudit($"{new string('-', 80)}{System.Environment.NewLine}{DateTimeOffset.UtcNow:O}  result  {outcome}  {result.ElapsedMilliseconds} ms{leftOpen}{System.Environment.NewLine}");
	}

	private static void AppendAudit(string text)
	{
		try
		{
			_ = Directory.CreateDirectory(BridgeLog.ReleaseDirectory);
			System.IO.File.AppendAllText(BridgeLog.AuditLogPath, text);
		}
		catch (Exception exception)
		{
			BridgeLog.Write($"Could not write the execution audit entry. {exception.Message}");
		}
	}
}
