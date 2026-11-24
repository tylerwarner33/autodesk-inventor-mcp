using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

internal static partial class InventorTool
{
	/// <summary>
	/// 	How long the stop waits for Inventor to end.
	/// </summary>
	/// <remarks>
	/// 	Stays under what clients wait for one call.
	/// </remarks>
	private static readonly TimeSpan _stopWaitLimit = TimeSpan.FromSeconds(30);

	/// <summary>
	/// 	Lists the documents with unsaved changes. A document that was never saved has no file name, so its display name
	/// 	is used.
	/// </summary>
	private const string UnsavedDocumentsSnippet = """
		var dirty = Application.Documents.OfType<Document>()
			.Where(document => document.Dirty)
			.Select(document => string.IsNullOrEmpty(document.FullFileName) ? document.DisplayName : document.FullFileName)
			.ToList();
		return System.Text.Json.JsonSerializer.Serialize(new { dirty, openDocuments = Application.Documents.Count });
		""";

	[McpServerTool(Name = "inventor_stop", Title = "Close an Inventor that this server started", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
	[Description("""
		Closes an Autodesk Inventor process that inventor_start started in this server, the same as a click on the close
		button of its window. Call it only when the user asks to close that Inventor.

		It never closes an Inventor that the user opened, or that another MCP client or an earlier server started. It
		closes only the Inventor that this session uses now. Give the processId from inventor_start or inventor_session.

		It refuses when a document in that Inventor has unsaved changes, and lists them: ask the user to save or close
		them first. It never forces a close. Other MCP clients that use the same Inventor lose it, so ask the user first
		when another client can be using it.

		It waits up to 30 s for the process to end. On waiting-for-user, a dialog holds the close: tell the user, and
		never answer it for them. After the close, this session uses no Inventor until inventor_use_release or
		inventor_start.
		""")]
	public static async Task<object> StopAsync(
		BridgeClient bridge,
		ReleaseSelection selection,
		StartedInventors started,
		IBlockingDialogs dialogs,
		[Description("The id of the Inventor process to close, from inventor_start or inventor_session.")] int processId,
		CancellationToken cancellationToken = default)
	{
		if (started.Contains(processId) is false)
		{
			return new
			{
				error = "not-started-here",
				message = $"This server did not start the Inventor process {processId}, or it no longer runs, so this server does not close it. " +
					"Nothing was closed. Ask the user to close that Inventor.",
				startedHere = started.Running()
			};
		}

		// The unsaved changes can be read only through the bridge of this session, so the process must be the one in use.
		int? sessionProcess = selection.ChosenProcessId ?? bridge.InventorProcessId;

		if (sessionProcess != processId)
		{
			return new
			{
				error = "not-this-session",
				message = $"This session uses {(sessionProcess is int used ? $"the Inventor process {used}" : "no Inventor process")}, not {processId}. " +
					$"Nothing was closed. To close {processId}, ask the user, then call inventor_use_release with its release and processId, and call inventor_stop again.",
				startedHere = started.Running()
			};
		}

		return await UnlessBlockedAsync(
			bridge,
			() => CloseIfSavedAsync(bridge, selection, dialogs, processId, cancellationToken),
			cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// 	Closes the process when no document has unsaved changes, and waits for it to end.
	/// </summary>
	private static async Task<object> CloseIfSavedAsync(BridgeClient bridge, ReleaseSelection selection, IBlockingDialogs dialogs, int processId, CancellationToken cancellationToken)
	{
		JsonElement documents;

		try
		{
			documents = await RunJsonSnippetAsync(bridge, UnsavedDocumentsSnippet, cancellationToken).ConfigureAwait(false);
		}
		catch (InventorBridgeException exception)
		{
			return new
			{
				error = exception.Code,
				message = $"The unsaved changes in the Inventor process {processId} could not be read, so nothing was closed. {exception.Message}"
			};
		}

		// The snippet must have run in the process that is closed, not in another one.
		if (bridge.InventorProcessId != processId || documents.TryGetProperty("dirty", out JsonElement dirtyList) is false)
		{
			return new
			{
				error = "unsaved-changes-unknown",
				message = $"The unsaved changes in the Inventor process {processId} could not be read, so nothing was closed. Tell the user.",
				detail = documents
			};
		}

		string[] dirty = [.. dirtyList.EnumerateArray().Select(static name => name.GetString() ?? string.Empty)];

		if (dirty.Length > 0)
		{
			return new
			{
				error = "unsaved-changes",
				message = $"These documents in the Inventor process {processId} have unsaved changes, so nothing was closed. " +
					"Ask the user to save or close them, then call inventor_stop again.",
				dirty
			};
		}

		using Process? inventor = OpenProcess(processId);

		if (inventor is null)
			return Closed(selection, processId);

		// The same message as a click on the close button: Inventor runs its own shutdown, and the add-in closes its pipe.
		if (inventor.CloseMainWindow() is false)
		{
			return new
			{
				error = "no-main-window",
				message = $"The Inventor process {processId} has no main window that takes a close request now (ex. a hidden instance, or a window " +
					"that a dialog disables). Nothing was closed. Tell the user."
			};
		}

		if (await WaitForExitAsync(inventor, _stopWaitLimit, cancellationToken).ConfigureAwait(false))
			return Closed(selection, processId);

		// A dialog can hold the close (ex. a question from an add-in). It needs a person.
		BlockState state = await Task.Run(() => dialogs.Detect(processId), cancellationToken).ConfigureAwait(false);

		if (state is { Blocked: true, Dialogs.Count: > 0 })
		{
			return new
			{
				status = "waiting-for-user",
				processId,
				message = $"The Inventor process {processId} got the close request, but a dialog waits for the user. Tell the user, and never " +
					"answer the dialog for them. Do not call inventor_stop again.",
				blockedByDialog = state.Summary,
				dialogs = state.Dialogs.Select(dialog => DescribeDialog(dialog, bridge.DialogSettings))
			};
		}

		return new
		{
			status = "still-closing",
			processId,
			message = $"The Inventor process {processId} got the close request, but it did not end within {_stopWaitLimit.TotalSeconds:0} s. " +
				"Check later with inventor_session. Do not call inventor_stop again."
		};
	}

	private static object Closed(ReleaseSelection selection, int processId) => new
	{
		status = "closed",
		processId,
		message = $"The Inventor process {processId} closed. This session uses no Inventor now. To use another one, ask the user, " +
			"then call inventor_use_release, or inventor_start when the user asks for a new Inventor.",
		runningInstances = Instances(selection)
	};

	private static Process? OpenProcess(int processId)
	{
		try
		{
			return Process.GetProcessById(processId);
		}
		catch (ArgumentException)
		{
			// No process has the id.
			return null;
		}
	}

	/// <summary>
	/// 	Waits for a process to end.
	/// </summary>
	/// <returns>
	/// 	True when the process ended within the wait.
	/// </returns>
	private static async Task<bool> WaitForExitAsync(Process process, TimeSpan wait, CancellationToken cancellationToken)
	{
		using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		limit.CancelAfter(wait);

		try
		{
			await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);

			return true;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is false)
		{
			return false;
		}
	}
}
