using System.Diagnostics;
using System.Runtime.InteropServices;

using Inventor;

using InventorMcp.AddIn.Bridge;
using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;

using Microsoft.CodeAnalysis.Scripting;

namespace InventorMcp.AddIn.Operations;

internal sealed partial class InventorOperations
{
	private readonly ScriptCache _scripts = new();

	// Used on Inventor's main thread only, as each audit entry is.
	private readonly SnippetAudit _audit = new();

	/// <summary>
	/// 	Runs a snippet that the bridge thread compiled.
	/// </summary>
	/// <remarks>
	/// 	The audit entry is written also for a snippet that did not compile, so the trail holds every snippet.
	/// </remarks>
	private ExecutionResult EvaluateCSharp(ExecuteRequest request, CompiledScript compiled)
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
		ExecutionResult result = RunScript(compiled, document);

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

	private ExecutionResult RunScript(CompiledScript compiled, Document? document)
	{
		InventorScriptGlobals globals = new() { Application = _inventor, Document = document };
		Stopwatch stopwatch = Stopwatch.StartNew();

		if (compiled.Runner is null)
		{
			return new ExecutionResult(
				false, null, null, globals.Output, compiled.Diagnostics,
				compiled.FailureType, compiled.FailureMessage, stopwatch.ElapsedMilliseconds);
		}

		try
		{
			using (SilentOperationScope.Enter(_inventor))
			{
				// The snippet body is synchronous, so the returned task completes inline and this cannot deadlock.
				object? returnValue = compiled.Runner(globals).GetAwaiter().GetResult();

				return new ExecutionResult(
					true,
					ReturnValueText.Format(returnValue),
					returnValue?.GetType().FullName,
					globals.Output,
					compiled.Diagnostics,
					null,
					null,
					stopwatch.ElapsedMilliseconds);
			}
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
	/// 	The snippet. The entry holds it in full the first time, and its SHA-256 each time.
	/// </param>
	/// <param name="clientName">
	/// 	The MCP client that sent it, or null.
	/// </param>
	private void WriteAuditEntry(string language, Document? document, string code, string? clientName)
	{
		// Written before the run, so the log holds the snippet even when the run takes Inventor down.
		AppendAudit(_audit.Entry(language, document?.DisplayName, code, clientName, DateTimeOffset.UtcNow));
	}

	/// <summary>
	/// 	Appends the outcome of the snippet that the last entry recorded.
	/// </summary>
	private void WriteAuditResult(ExecutionResult result)
	{
		string outcome = result.Succeeded ? "succeeded" : $"failed  {result.ExceptionType}: {result.ExceptionMessage}";
		string leftOpen = result.DocumentsLeftOpen is { Count: > 0 } documents ? $"  left open: {string.Join(", ", documents)}" : string.Empty;

		AppendAudit($"{new string('-', 80)}{System.Environment.NewLine}{DateTimeOffset.UtcNow:O}  result  {outcome}  {result.ElapsedMilliseconds} ms{leftOpen}{System.Environment.NewLine}");
	}

	private void AppendAudit(string text)
	{
		try
		{
			_ = Directory.CreateDirectory(BridgeLog.ReleaseDirectory);

			// A new file must hold the full code of each snippet again, so it can be read without the files before it.
			if (RollingFile.Append(BridgeLog.AuditLogPath, text))
				_audit.Forget();
		}
		catch (Exception exception)
		{
			BridgeLog.Write($"Could not write the execution audit entry. {exception.Message}");
		}
	}
}
