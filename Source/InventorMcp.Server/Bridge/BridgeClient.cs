using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using InventorMcp.Contracts;
using InventorMcp.Server.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace InventorMcp.Server.Bridge;

/// <summary>
/// 	Talks to the Inventor add-in over the named pipe.
/// </summary>
/// <remarks>
/// 	Requests are serialised one at a time.
/// 	Everything they reach lands on Inventor's single main thread anyway, so overlapping them would only queue deeper.
/// 	A wait can stop before its response arrives (ex. on a blocking dialog), so each response is matched to its
/// 	request by ID, and a late response is discarded.
/// </remarks>
/// <param name="logger">
/// 	The server log.
/// </param>
/// <param name="dialogs">
/// 	Finds a modal dialog that blocks Inventor while a call waits.
/// </param>
/// <param name="timeProvider">
/// 	The clock of the dialog watchdog.
/// </param>
/// <param name="selection">
/// 	Gives the pipe of the release this session uses. A change of the release closes the connection.
/// </param>
/// <param name="dialogSettings">
/// 	Reads the button to click on each dialog type, or null for <see cref="DialogSettings.Load"/>.
/// </param>
internal sealed class BridgeClient(
	ILogger<BridgeClient> logger,
	IBlockingDialogs dialogs,
	TimeProvider timeProvider,
	ReleaseSelection selection,
	Func<DialogSettings>? dialogSettings = null) : IAsyncDisposable
{
	/// <summary>
	/// 	The wait before the first dialog check. Most calls return before it.
	/// </summary>
	public static readonly TimeSpan FirstDialogCheck = TimeSpan.FromSeconds(3);

	/// <summary>
	/// 	The time between dialog checks after the first.
	/// </summary>
	public static readonly TimeSpan DialogCheckInterval = TimeSpan.FromSeconds(2);

	private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

	/// <summary>
	/// 	The number of checks in a row that must see a block with no dialog of Inventor before the call stops.
	/// </summary>
	/// <remarks>
	/// 	The main frame is also disabled for a moment while a dialog opens or closes.
	/// </remarks>
	private const int _checksBeforeBlockWithoutDialog = 2;

	private const int _maxTextInMessage = 600;

	private readonly ILogger<BridgeClient> _logger = logger;
	private readonly IBlockingDialogs _dialogs = dialogs;
	private readonly TimeProvider _timeProvider = timeProvider;
	private readonly ReleaseSelection _selection = selection;
	private readonly Func<DialogSettings> _dialogSettings = dialogSettings ?? DialogSettings.Load;
	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <summary>
	/// 	Inventor release of the connected pipe, ex. 2025, or null with no connection.
	/// </summary>
	public int? ReleaseYear { get; private set; }

	/// <summary>
	/// 	The release that version specific data, such as the API documentation, follows.
	/// </summary>
	/// <remarks>
	/// 	The connected release when there is one, and else the release this session chose. Null when neither is known.
	/// </remarks>
	public int? EffectiveReleaseYear => ReleaseYear ?? _selection.Chosen;

	/// <summary>
	/// 	The process that hosts the add-in, from the pipe, or null with no connection.
	/// </summary>
	/// <remarks>
	/// 	It comes from the pipe and not from a bridge call, because a bridge call waits on a blocking dialog too.
	/// </remarks>
	public int? InventorProcessId { get; private set; }

	/// <summary>
	/// 	The MCP client that this server serves, ex. "claude-code 2.1.282", or null before its first tool call.
	/// </summary>
	/// <remarks>
	/// 	Set from the <c>initialize</c> request by the call tool filter in <c>Program.cs</c>.
	/// 	Each client starts its own server, so one name holds for the whole process.
	/// </remarks>
	public string? ClientName { get; set; }

	private int _connectedGeneration;

	/// <summary>
	/// 	The release an automatic selection connected to, so a reconnect never moves to another release unasked.
	/// </summary>
	/// <remarks>
	/// 	Cleared when the selection changes, ex. <c>inventor_use_release</c> with 0.
	/// </remarks>
	private int? _automaticRelease;

	private NamedPipeClientStream? _pipe;
	private StreamReader? _reader;
	private StreamWriter? _writer;

	/// <summary>
	/// 	The read of the next response line.
	/// </summary>
	/// <remarks>
	/// 	A wait that stops early leaves the read running, and the next call continues it.
	/// 	A cancelled <see cref="StreamReader.ReadLineAsync(CancellationToken)"/> is not used, because it can leave the
	/// 	reader unusable.
	/// </remarks>
	private Task<string?>? _pendingLine;

	/// <summary>
	/// 	Sends one operation and returns its typed result.
	/// </summary>
	/// <typeparam name="TResult">
	/// 	Type the operation returns.
	/// </typeparam>
	/// <param name="operation">
	/// 	A name from <see cref="BridgeOperations"/>.
	/// </param>
	/// <param name="payload">
	/// 	Operation arguments, or null.
	/// </param>
	/// <param name="cancellationToken">
	/// 	Cancels the call.
	/// </param>
	/// <returns>
	/// 	The deserialised result.
	/// </returns>
	/// <exception cref="InventorBridgeException">
	/// 	The add-in reported a failure, or a dialog that needs a person blocks Inventor
	/// 	(<see cref="BridgeErrorCodes.BlockedByDialog"/>).
	/// </exception>
	public async Task<TResult> InvokeAsync<TResult>(string operation, object? payload, CancellationToken cancellationToken)
	{
		// One place for the client name, so no tool that composes a request has to pass it.
		if (payload is ExecuteRequest { ClientName: null } execute && ClientName is not null)
			payload = execute with { ClientName = ClientName };

		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			// A dropped pipe surfaces only on use, so one reconnect and retry is expected rather than exceptional.
			TResult result;

			try
			{
				result = await SendAsync<TResult>(operation, payload, cancellationToken).ConfigureAwait(false);
			}
			catch (IOException exception)
			{
				_logger.LogWarning(exception, "The bridge connection dropped. Reconnecting once.");

				await CloseAsync().ConfigureAwait(false);

				result = await SendAsync<TResult>(operation, payload, cancellationToken).ConfigureAwait(false);
			}

			if (result is Contracts.Models.SessionInfo session)
				ReleaseYear = session.ReleaseYear;

			return result;
		}
		finally
		{
			_ = _gate.Release();
		}
	}

	/// <summary>
	/// 	Connects to the pipe without sending a request.
	/// </summary>
	/// <remarks>
	/// 	A connection proves the add-in is hosting the bridge.
	/// 	A request could block on a dialog that holds Inventor's main thread at startup, so none is sent.
	/// </remarks>
	/// <param name="cancellationToken">
	/// 	Cancels the attempt.
	/// </param>
	/// <returns>
	/// 	True when the pipe is connected.
	/// </returns>
	public async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

			return true;
		}
		catch (InventorBridgeException exception) when (exception.Code == BridgeErrorCodes.NotRunning)
		{
			return false;
		}
		finally
		{
			_ = _gate.Release();
		}
	}

	/// <summary>
	/// 	Examines the connected Inventor for a blocking dialog, with no bridge call.
	/// </summary>
	/// <remarks>
	/// 	Does not wait for a call that is in progress, so a tool can report the block at once.
	/// </remarks>
	/// <param name="cancellationToken">
	/// 	Cancels the connection attempt.
	/// </param>
	/// <returns>
	/// 	The block state, or null when no Inventor hosts the bridge.
	/// </returns>
	public async Task<BlockState?> GetBlockStateAsync(CancellationToken cancellationToken)
	{
		if ((InventorProcessId is null || _connectedGeneration != _selection.Generation)
			&& await TryConnectAsync(cancellationToken).ConfigureAwait(false) is false)
			return null;

		return InventorProcessId is int processId
			? await Task.Run(() => _dialogs.Detect(processId), cancellationToken).ConfigureAwait(false)
			: null;
	}

	private async Task<TResult> SendAsync<TResult>(string operation, object? payload, CancellationToken cancellationToken)
	{
		await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

		JsonElement? serialisedPayload = payload is null
			? null
			: JsonSerializer.SerializeToElement(payload, BridgeProtocol.SerializerOptions);

		BridgeRequest request = new(Guid.NewGuid().ToString("N"), operation, serialisedPayload);

		// A dialog's modal loop still runs the add-in's work, so a call sent now would run nested inside the call that
		// opened the dialog (ex. inside a waiting iLogic rule). So the dialog is handled first, and nothing is sent while
		// one needs a person.
		DialogWatch watch = new();
		await HandleDialogsAsync(watch, cancellationToken).ConfigureAwait(false);

		await _writer!.WriteLineAsync(JsonSerializer.Serialize(request, BridgeProtocol.SerializerOptions)).ConfigureAwait(false);

		BridgeResponse response = await ReadResponseAsync(request.Id, watch, cancellationToken).ConfigureAwait(false);

		if (response.Success is false)
		{
			BridgeError error = response.Error ?? new BridgeError(BridgeErrorCodes.Internal, "The bridge reported a failure with no detail.");

			throw new InventorBridgeException(error.Code, error.Message, error.Detail);
		}

		if (response.Result is not JsonElement result || result.ValueKind is JsonValueKind.Null)
			return default!;

		return JsonSerializer.Deserialize<TResult>(result, BridgeProtocol.SerializerOptions)!;
	}

	/// <summary>
	/// 	Waits for the response to one request, and examines Inventor for a blocking dialog while it waits.
	/// </summary>
	/// <remarks>
	/// 	A modal dialog holds the main thread, so the call that opened it is also the call that waits on it.
	/// 	So the check runs on a timer here, not after the call returns.
	/// 	See <c>Docs/Research/Blocking-Dialog-Detection.md</c>, "Proposal for the server".
	/// </remarks>
	private async Task<BridgeResponse> ReadResponseAsync(string requestId, DialogWatch watch, CancellationToken cancellationToken)
	{
		TimeSpan wait = FirstDialogCheck;

		while (true)
		{
			_pendingLine ??= _reader!.ReadLineAsync();

			using CancellationTokenSource delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			Task delay = Task.Delay(wait, _timeProvider, delayCancellation.Token);

			if (await Task.WhenAny(_pendingLine, delay).ConfigureAwait(false) == _pendingLine)
			{
				await delayCancellation.CancelAsync().ConfigureAwait(false);

				BridgeResponse response = await TakeResponseAsync().ConfigureAwait(false);

				if (response.Id == requestId)
					return response;

				_logger.LogWarning(
					"Discarded the late response {ResponseId} from a call that stopped waiting. Waiting for {RequestId}.",
					response.Id,
					requestId);

				continue;
			}

			cancellationToken.ThrowIfCancellationRequested();

			wait = DialogCheckInterval;

			await HandleDialogsAsync(watch, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task<BridgeResponse> TakeResponseAsync()
	{
		Task<string?> read = _pendingLine!;
		_pendingLine = null;

		string? line = await read.ConfigureAwait(false)
			?? throw new IOException("The Inventor bridge closed the connection.");

		return JsonSerializer.Deserialize<BridgeResponse>(line, BridgeProtocol.SerializerOptions)
			?? throw new InventorBridgeException(BridgeErrorCodes.Internal, "The bridge returned an empty response.");
	}

	/// <summary>
	/// 	Closes an information dialog, or stops the wait on a dialog that needs a person.
	/// </summary>
	private async Task HandleDialogsAsync(DialogWatch watch, CancellationToken cancellationToken)
	{
		if (InventorProcessId is not int processId)
			return;

		BlockState state = await Task.Run(() => _dialogs.Detect(processId), cancellationToken).ConfigureAwait(false);

		if (state.Blocked is false)
		{
			watch.ChecksWithoutDialog = 0;
			return;
		}

		if (state.Dialogs.Count == 0)
		{
			if (++watch.ChecksWithoutDialog < _checksBeforeBlockWithoutDialog)
				return;

			_logger.LogWarning("Inventor {ProcessId} is blocked, but no dialog of its process was found.", processId);

			throw new InventorBridgeException(
				BridgeErrorCodes.BlockedByDialog,
				"Inventor is blocked: its main window is disabled, but it has no dialog with a title. A window of a " +
				"different process (ex. Vault or a licence service) can cause this. Ask the user to look at the screen.");
		}

		DialogSettings settings = _dialogSettings();

		foreach (string problem in settings.Problems)
			_logger.LogWarning("Dialog settings: {Problem}", problem);

		List<DialogSnapshot> needPerson = [];

		foreach (DialogSnapshot dialog in state.Dialogs)
		{
			// A dialog that is still open after the server clicked it needs a person.
			string? button = watch.Clicked.Contains((dialog.Handle, dialog.Title)) ? null : DialogPolicy.ButtonToClick(dialog, settings);

			if (button is null)
			{
				needPerson.Add(dialog);
				continue;
			}

			_ = watch.Clicked.Add((dialog.Handle, dialog.Title));

			ClickOutcome outcome = await Task.Run(
				() => _dialogs.TryClick(processId, dialog, button),
				cancellationToken).ConfigureAwait(false);

			string action = outcome.Status switch
			{
				// The model must know what a click changed, ex. files saved in a format that older releases cannot open.
				ClickStatus.Clicked => $"closed with {button}{DialogPolicy.ClickConsequence(DialogPolicy.Classify(dialog), button)}",
				ClickStatus.DialogClosed => "closed by a different server or the user",
				_ => $"left open: {outcome.Message}"
			};

			Report(processId, dialog, action);

			if (outcome.Status is ClickStatus.Clicked)
				WriteClickAudit(dialog, button);
			else if (outcome.Status is ClickStatus.Refused or ClickStatus.Failed)
				needPerson.Add(dialog);
		}

		if (needPerson.Count == 0)
			return;

		foreach (DialogSnapshot dialog in needPerson)
			Report(processId, dialog, "left open for a person");

		throw BlockedByDialog(needPerson[0]);
	}

	private void Report(int processId, DialogSnapshot dialog, string action)
	{
		string? type = DialogPolicy.Classify(dialog);

		// The text is gone when the dialog closes, and iLogic does not show the same error again for 120 minutes.
		_logger.LogWarning(
			"A dialog blocks Inventor {ProcessId}: '{Title}' ({Framework}, {Type}, class {ClassName}). Buttons: {Buttons}. " +
			"Action: {Action}. {ReadError}Text: {Text}",
			processId,
			dialog.Title,
			dialog.Framework ?? "not read",
			type ?? "not in the catalog",
			dialog.ClassName,
			string.Join(", ", dialog.ClickableButtons),
			action,
			dialog.ReadError is null ? string.Empty : dialog.ReadError + " ",
			dialog.Text);

		DialogReports.Add(new DialogReport(dialog.Title, type, dialog.Text, dialog.ClickableButtons, action));
	}

	private static InventorBridgeException BlockedByDialog(DialogSnapshot dialog)
	{
		string? type = DialogPolicy.Classify(dialog);
		string text = dialog.Text.Length > _maxTextInMessage ? dialog.Text[.._maxTextInMessage] + " ..." : dialog.Text;

		string message =
			"Inventor is blocked by a modal dialog. The call cannot run until the dialog is closed." + Environment.NewLine +
			$"Dialog: '{dialog.Title}' ({dialog.Framework ?? "not read"}, {type ?? "not in the catalog"})" + Environment.NewLine +
			(dialog.ReadError is null ? $"Text: {text}" : dialog.ReadError) + Environment.NewLine +
			$"Visible buttons: {string.Join(", ", dialog.ClickableButtons)}" + Environment.NewLine +
			(dialog.Options.Count == 0 ? string.Empty : $"Options: {DescribeOptions(dialog.Options)}" + Environment.NewLine) +
			"Do not close a dialog that asks a question without asking the user. Read it with inventor_dialogs, and close it " +
			"with inventor_dialog_click only after the user chose the button.";

		return new InventorBridgeException(BridgeErrorCodes.BlockedByDialog, message, dialog.Text);
	}

	/// <summary>
	/// 	Lists the options with the selected ones marked, ex. <c>[x] Assume that this external rule is safe</c>.
	/// </summary>
	internal static string DescribeOptions(IReadOnlyList<DialogOption> options) =>
		string.Join(", ", options.Select(option => $"{(option.IsSelected ? "[x]" : "[ ]")} {option.Name}"));

	private void WriteClickAudit(DialogSnapshot dialog, string button)
	{
		try
		{
			ExecutionAuditLog.WriteDialogClick(dialog, button, "watchdog", ReleaseYear);
		}
		catch (IOException exception)
		{
			_logger.LogWarning(exception, "Could not write the dialog click to the audit log.");
		}
	}

	private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
	{
		// A change of the release makes the open connection stale, even when it still works.
		if (_pipe is { IsConnected: true } && _connectedGeneration == _selection.Generation)
			return;

		await CloseAsync().ConfigureAwait(false);

		if (_connectedGeneration != _selection.Generation)
			_automaticRelease = null;

		_connectedGeneration = _selection.Generation;

		PipeResolution resolution = _selection.Resolve();

		if (resolution.PipeName is not string pipeName)
		{
			throw new InventorBridgeException(
				resolution.ErrorCode!,
				resolution.ErrorCode == BridgeErrorCodes.NotRunning ? NotRunningMessage(_automaticRelease) : resolution.Message!);
		}

		// The release this session used closed, and a different one is the only pipe now. The documents differ, so a call
		// (ex. the retry of a write after the pipe dropped) must not go there without a choice.
		if (_selection.Chosen is null && _automaticRelease is int previous && resolution.ReleaseYear != previous)
		{
			throw new InventorBridgeException(
				BridgeErrorCodes.ReleaseRequired,
				$"Autodesk Inventor {previous}, which this session used, no longer hosts the MCP bridge. Autodesk Inventor " +
				$"{resolution.ReleaseYear} does, but this session does not move to another release unasked. Ask the user which " +
				$"release to use, then call inventor_use_release (ex. with {resolution.ReleaseYear}), or start {previous} again.");
		}

		NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

		try
		{
			await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			await pipe.DisposeAsync().ConfigureAwait(false);

			// The code stays NotRunning even with Inventor open, because inventor_start polls on it while Inventor loads.
			// Only the message tells the model not to start a second Inventor.
			throw new InventorBridgeException(BridgeErrorCodes.NotRunning, NotRunningMessage(resolution.ReleaseYear));
		}

		_pipe = pipe;
		ReleaseYear = resolution.ReleaseYear;

		if (_selection.Chosen is null)
			_automaticRelease = resolution.ReleaseYear;
		_reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
		_writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
		InventorProcessId = GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId) ? (int)processId : null;

		_logger.LogInformation(
			"Connected to the Inventor bridge on pipe {PipeName}, hosted by process {ProcessId}.",
			pipeName,
			InventorProcessId);
	}

	/// <summary>
	/// 	Says why no bridge answered, and counts only the Inventor processes of the release that was asked for.
	/// </summary>
	private static string NotRunningMessage(int? releaseYear)
	{
		string subject = releaseYear is int year ? $"Autodesk Inventor {year}" : "Inventor";

		return InventorInstallations.FindRunning(releaseYear).Count > 0
			? $"{subject} is running, but its MCP bridge accepted no connection within " +
				$"{ConnectTimeout.TotalSeconds:0} s. The add-in may still be loading or may not be loaded, or every bridge " +
				"connection may be in use by other MCP clients. Do not call inventor_start. Retry shortly, and if it " +
				"persists, ask the user to check Tools > Add-Ins in Inventor or to close another MCP client."
			: releaseYear is null
				? "No Inventor session is hosting the MCP bridge. Ask the user whether to start Inventor, then call " +
					"inventor_start. If Inventor is already open, make sure the Inventor MCP Bridge add-in is loaded."
				: $"{subject} is not hosting the MCP bridge. This session uses that release only, and does not fall back to " +
					$"another one. Ask the user whether to start it, then call inventor_start with version {releaseYear}. " +
					"Or call inventor_use_release to use a different release.";
	}

	private async Task CloseAsync()
	{
		// Disposing the writer flushes, which throws on a pipe whose Inventor has closed.
		// Closing must still succeed, or the reconnect after it never runs.
		try
		{
			if (_writer is not null)
				await _writer.DisposeAsync().ConfigureAwait(false);
		}
		catch (IOException)
		{
		}

		_reader?.Dispose();

		try
		{
			if (_pipe is not null)
				await _pipe.DisposeAsync().ConfigureAwait(false);
		}
		catch (IOException)
		{
		}

		_writer = null;
		_reader = null;
		_pipe = null;

		// A read of the closed pipe ends with an exception that nobody waits for.
		_ = _pendingLine?.ContinueWith(static read => _ = read.Exception, TaskScheduler.Default);
		_pendingLine = null;

		// The next connection may be a different Inventor release.
		ReleaseYear = null;
		InventorProcessId = null;
	}

	public async ValueTask DisposeAsync()
	{
		await CloseAsync().ConfigureAwait(false);
		_gate.Dispose();
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

	/// <summary>
	/// 	The dialog state of one wait.
	/// </summary>
	private sealed class DialogWatch
	{
		public HashSet<(long Handle, string Title)> Clicked { get; } = [];

		public int ChecksWithoutDialog { get; set; }
	}
}

/// <summary>
/// 	A failure the Inventor bridge reported, or a failure reaching it.
/// </summary>
internal sealed class InventorBridgeException(string code, string message, string? detail = null) : Exception(message)
{
	/// <summary>
	/// 	A value from <see cref="BridgeErrorCodes"/>.
	/// </summary>
	public string Code { get; } = code;

	/// <summary>
	/// 	Extra context, or null.
	/// </summary>
	public string? Detail { get; } = detail;
}