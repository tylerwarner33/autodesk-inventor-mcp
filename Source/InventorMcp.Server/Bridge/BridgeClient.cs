using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using InventorMcp.Contracts;
using InventorMcp.Server.Services;
using InventorMcp.Server.Telemetry;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace InventorMcp.Server.Bridge;

/// <summary>
/// 	Talks to the Inventor add-in over the named pipe, as an MCP client whose methods are the bridge operations.
/// </summary>
/// <remarks>
/// 	Requests that use Inventor's main thread go one at a time.
/// 	Everything they reach lands on that single thread anyway, so overlapping them would only queue deeper.
/// 	The add-in also runs the requests of one connection one at a time, because a cancel frees this gate before the
/// 	cancelled request ends in the add-in. <see cref="BridgeOperations.RunsWithoutMainThread"/> skips both.
/// 	The SDK matches each response to its request by id, so a wait that stops early (ex. on a blocking dialog) leaves
/// 	nothing behind for the next call.
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
/// 	Gives the button to click on each dialog type, or null for <see cref="DialogSettings.Load"/>, called one time here.
/// </param>
/// <param name="checkHost">
/// 	Gives why the process that hosts the pipe is not this user's Inventor, or null when it is.
/// 	Null for <see cref="PipeHost.Check(int)"/>.
/// </param>
/// <param name="findRunning">
/// 	Gives the Inventor processes of a release, or of every release for null.
/// 	Null for <see cref="InventorInstallations.FindRunning(int?)"/>.
/// </param>
/// <param name="isAddInDeployed">
/// 	Tells if a manifest of the add-in exists for a release, to tell why a connect failed.
/// 	Null for <see cref="InventorInstallations.IsAddInDeployed(int)"/>.
/// </param>
/// <param name="processRuns">
/// 	Gives whether the process with an ID still runs. Null for <see cref="ProcessRuns(int)"/>.
/// </param>
internal sealed partial class BridgeClient(
	ILogger<BridgeClient> logger,
	IBlockingDialogs dialogs,
	TimeProvider timeProvider,
	ReleaseSelection selection,
	Func<DialogSettings>? dialogSettings = null,
	Func<int, string?>? checkHost = null,
	Func<int?, IReadOnlyList<RunningInventor>>? findRunning = null,
	Func<int, bool>? isAddInDeployed = null,
	Func<int, bool>? processRuns = null) : IAsyncDisposable
{
	/// <summary>
	/// 	The wait before the first dialog check. Most calls return before it.
	/// </summary>
	public static readonly TimeSpan FirstDialogCheck = TimeSpan.FromSeconds(3);

	/// <summary>
	/// 	The time between dialog checks after the first.
	/// </summary>
	public static readonly TimeSpan DialogCheckInterval = TimeSpan.FromSeconds(2);

	private static readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(3);

	/// <summary>
	/// 	The limit of the MCP handshake after the pipe connects.
	/// </summary>
	/// <remarks>
	/// 	The handshake does not use Inventor's main thread, so it answers during a start and under a dialog. An add-in that
	/// 	does not answer in this time speaks another protocol (a build from before MCP over the pipe).
	/// </remarks>
	private static readonly TimeSpan _handshakeTimeout = TimeSpan.FromSeconds(10);

	/// <summary>
	/// 	The MCP client options of every connection, with the pinned version of <see cref="BridgeProtocol.McpVersion"/>.
	/// </summary>
	private static readonly McpClientOptions _clientOptions = new()
	{
		ClientInfo = new Implementation { Name = "InventorMcp.Server", Version = BridgeProtocol.Version.ToString(CultureInfo.InvariantCulture) },
		ProtocolVersion = BridgeProtocol.McpVersion
	};

	/// <summary>
	/// 	The number of checks in a row that must see a block with no dialog of Inventor before the call stops.
	/// </summary>
	/// <remarks>
	/// 	The main frame is also disabled for a moment while a dialog opens or closes.
	/// </remarks>
	private const int ChecksBeforeBlockWithoutDialog = 2;

	private const int MaxTextInMessage = 600;

	private readonly ILogger<BridgeClient> _logger = logger;
	private readonly IBlockingDialogs _dialogs = dialogs;
	private readonly TimeProvider _timeProvider = timeProvider;
	private readonly ReleaseSelection _selection = selection;
	private readonly Func<DialogSettings> _dialogSettings = dialogSettings ?? LoadOnce();
	private readonly Func<int, string?> _checkHost = checkHost ?? PipeHost.Check;
	private readonly Func<int?, IReadOnlyList<RunningInventor>> _findRunning = findRunning ?? InventorInstallations.FindRunning;
	private readonly Func<int, bool> _isAddInDeployed = isAddInDeployed ?? InventorInstallations.IsAddInDeployed;
	private readonly Func<int, bool> _processRuns = processRuns ?? ProcessRuns;
	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <summary>
	/// 	Guards the connect, the reconnect and the close.
	/// </summary>
	/// <remarks>
	/// 	<see cref="_gate"/> does not guard every use of the connection, because an operation that runs without the main
	/// 	thread skips it.
	/// </remarks>
	private readonly SemaphoreSlim _connectLock = new(1, 1);

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
	/// 	True when the pipe is connected, for the gauge <c>inventor.bridge.connected</c>.
	/// </summary>
	internal bool IsConnected => HasLiveConnection;

	/// <summary>
	/// 	True when the pipe is connected and the Inventor that hosts it still runs.
	/// </summary>
	/// <remarks>
	/// 	A client pipe stays connected after its host process ended, until a read or a write fails. Without the process
	/// 	check, inventor_start took the pipe of an Inventor that had closed as the bridge of the new one, and the dialog
	/// 	tools examined the old process. See <c>Docs/Research/Blocking-Dialog-Detection.md</c>, "The Vault "Log In" dialog".
	/// </remarks>
	private bool HasLiveConnection =>
		_pipe is { IsConnected: true }
		&& _client is { Completion.IsCompleted: false }
		&& InventorProcessId is int processId
		&& _processRuns(processId);

	/// <summary>
	/// 	The MCP client that this server serves, ex. "claude-code 2.1.282", or null before its first tool call.
	/// </summary>
	/// <remarks>
	/// 	Set from the <c>initialize</c> request by the call tool filter in <c>Program.cs</c>.
	/// 	Each client starts its own server, so one name holds for the whole process.
	/// </remarks>
	public string? ClientName { get; set; }

	/// <summary>
	/// 	The dialog settings that the watchdog and the dialog tools use.
	/// </summary>
	public DialogSettings DialogSettings => _dialogSettings();

	private int _connectedGeneration;

	/// <summary>
	/// 	The release an automatic selection connected to, so a reconnect never moves to another release unasked.
	/// </summary>
	/// <remarks>
	/// 	Cleared when the selection changes, ex. <c>inventor_use_release</c> with 0.
	/// </remarks>
	private int? _automaticRelease;

	/// <summary>
	/// 	The Inventor process that this session connected to, so a reconnect goes to this process only.
	/// </summary>
	/// <remarks>
	/// 	The retry after a dropped pipe sends the same request again. With two Inventor processes of one release, a
	/// 	different process would run that request on other documents. Cleared when the selection changes.
	/// 	A new call goes on to the only process that is left (see <see cref="ReleaseSelection.Resolve"/>), so a restart of
	/// 	Inventor needs no choice.
	/// </remarks>
	private int? _lockedProcess;

	private NamedPipeClientStream? _pipe;

	/// <summary>
	/// 	The MCP session on <see cref="_pipe"/>, or null with no connection.
	/// </summary>
	private McpClient? _client;

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

		// An operation that never uses the main thread does not wait behind a call that does, and cannot meet a dialog.
		bool usesMainThread = BridgeOperations.RunsWithoutMainThread(operation) is false;

		if (usesMainThread)
		{
			long waitStarted = Stopwatch.GetTimestamp();
			await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			ServerTelemetry.RecordQueueWait(Stopwatch.GetElapsedTime(waitStarted), ReleaseYear);
		}

		using Activity? span = ServerTelemetry.StartBridgeCall(operation);
		long started = Stopwatch.GetTimestamp();
		string outcome = TelemetryTags.OutcomeException;
		string? errorType = null;

		try
		{
			// A dropped pipe surfaces only on use, so one reconnect and retry is expected rather than exceptional.
			TResult result;

			try
			{
				result = await SendAsync<TResult>(operation, payload, usesMainThread, retrying: false, cancellationToken).ConfigureAwait(false);
			}
			catch (IOException exception)
			{
				// The process id is read before the close, because the close clears it.
				bool? hostStillRuns = InventorProcessId is int droppedProcess ? _processRuns(droppedProcess) : null;

				LogConnectionDropped(exception, ConnectionDiagnosis.DescribeDrop(hostStillRuns));
				_ = span?.AddEvent(new ActivityEvent("reconnect"));

				await CloseAsync().ConfigureAwait(false);

				result = await SendAsync<TResult>(operation, payload, usesMainThread, retrying: true, cancellationToken).ConfigureAwait(false);
			}

			if (result is Contracts.Models.SessionInfo session)
				ReleaseYear = session.ReleaseYear;

			outcome = TelemetryTags.OutcomeOk;

			return result;
		}
		catch (InventorBridgeException exception)
		{
			outcome = TelemetryTags.OutcomeBridgeError;
			errorType = TelemetryTags.BridgeErrorValue(exception.Code);

			throw;
		}
		catch (Exception exception)
		{
			errorType = TelemetryTags.ExceptionTypeValue(exception.GetType().FullName);

			throw;
		}
		finally
		{
			ServerTelemetry.RecordBridgeCall(span, operation, outcome, errorType, Stopwatch.GetElapsedTime(started), ReleaseYear);

			if (usesMainThread)
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
			_ = await EnsureConnectedAsync(retrying: false, cancellationToken).ConfigureAwait(false);

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
	/// 	The block state, or null when no Inventor hosts the bridge and none is starting.
	/// </returns>
	public async Task<BlockState?> GetBlockStateAsync(CancellationToken cancellationToken)
	{
		if ((HasLiveConnection is false || _connectedGeneration != _selection.Generation)
			&& await TryConnectAsync(cancellationToken).ConfigureAwait(false) is false)
		{
			return await GetStartingBlockStateAsync(cancellationToken).ConfigureAwait(false);
		}

		return InventorProcessId is int processId
			? await Task.Run(() => _dialogs.Detect(processId), cancellationToken).ConfigureAwait(false)
			: null;
	}

	/// <summary>
	/// 	Examines an Inventor that does not host the bridge yet for a blocking dialog, with no connection attempt.
	/// </summary>
	/// <remarks>
	/// 	A dialog at the start (ex. the Vault "Log In") can come before the add-in loads, so the pipe cannot give the
	/// 	process. The process then comes from the process list, for the release this session chose. With more than one
	/// 	candidate, the one that is starting is not known, so none is examined.
	/// </remarks>
	/// <param name="cancellationToken">
	/// 	Cancels the examination.
	/// </param>
	/// <returns>
	/// 	The block state, or null when the bridge is connected, or not exactly one Inventor of the release runs.
	/// </returns>
	public async Task<BlockState?> GetStartingBlockStateAsync(CancellationToken cancellationToken)
	{
		if (HasLiveConnection)
			return null;

		int? release = _selection.Chosen;
		IReadOnlyList<RunningInventor> running = await Task.Run(() => _findRunning(release), cancellationToken).ConfigureAwait(false);

		return running is [RunningInventor only]
			? await Task.Run(() => _dialogs.Detect(only.ProcessId), cancellationToken).ConfigureAwait(false)
			: null;
	}

	private async Task<TResult> SendAsync<TResult>(string operation, object? payload, bool usesMainThread, bool retrying, CancellationToken cancellationToken)
	{
		McpClient client = await EnsureConnectedAsync(retrying, cancellationToken).ConfigureAwait(false);

		JsonRpcRequest request = new()
		{
			Method = operation,
			Params = payload is null ? null : JsonSerializer.SerializeToNode(payload, BridgeProtocol.SerializerOptions)
		};

		DialogWatch watch = new();

		// A dialog's modal loop still runs the add-in's work, so a call sent now would run nested inside the call that
		// opened the dialog (ex. inside a waiting iLogic rule). So the dialog is handled first, and nothing is sent while
		// one needs a person. An operation without the main thread runs nothing in that loop.
		if (usesMainThread)
			await HandleDialogsAsync(watch, cancellationToken).ConfigureAwait(false);

		// The token of the call goes to the SDK, which then sends the cancel to the add-in. A stop of the wait on a dialog
		// does not cancel the request: the call that opened the dialog already runs.
		Task<JsonRpcResponse> send = client.SendRequestAsync(request, cancellationToken);

		try
		{
			JsonRpcResponse response = usesMainThread
				? await WaitForResponseAsync(send, watch, cancellationToken).ConfigureAwait(false)
				: await send.ConfigureAwait(false);

			return BridgeResult.Unwrap<TResult>(response.Result)!;
		}
		catch (McpProtocolException exception)
		{
			throw ToBridgeException(exception, operation);
		}
		finally
		{
			// A wait that stopped early leaves the request running. Its end, a result or an exception, has no reader.
			if (send.IsCompleted is false)
				_ = send.ContinueWith(static abandoned => _ = abandoned.Exception, TaskScheduler.Default);
		}
	}

	/// <summary>
	/// 	Waits for the response to one request, and examines Inventor for a blocking dialog while it waits.
	/// </summary>
	/// <remarks>
	/// 	A modal dialog holds the main thread, so the call that opened it is also the call that waits on it.
	/// 	So the check runs on a timer here, not after the call returns.
	/// 	See <c>Docs/Architecture.md</c>, "The server watches for dialogs from outside Inventor".
	/// </remarks>
	private async Task<JsonRpcResponse> WaitForResponseAsync(Task<JsonRpcResponse> send, DialogWatch watch, CancellationToken cancellationToken)
	{
		TimeSpan wait = FirstDialogCheck;

		while (true)
		{
			using CancellationTokenSource delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			Task delay = Task.Delay(wait, _timeProvider, delayCancellation.Token);

			if (await Task.WhenAny(send, delay).ConfigureAwait(false) == send)
			{
				await delayCancellation.CancelAsync().ConfigureAwait(false);

				return await send.ConfigureAwait(false);
			}

			cancellationToken.ThrowIfCancellationRequested();

			wait = DialogCheckInterval;

			await HandleDialogsAsync(watch, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// 	Turns an error of the add-in into the bridge code that the tools report.
	/// </summary>
	/// <param name="exception">
	/// 	The error of the response.
	/// </param>
	/// <param name="operation">
	/// 	The operation that failed.
	/// </param>
	/// <returns>
	/// 	The exception with the code from <c>error.data.code</c>, or <see cref="BridgeErrorCodes.UnknownOperation"/> for a
	/// 	method that the add-in does not know.
	/// </returns>
	internal static InventorBridgeException ToBridgeException(McpProtocolException exception, string operation)
	{
		// A new operation does not change the protocol version, so an add-in of the same version can lack it.
		if (exception.ErrorCode is McpErrorCode.MethodNotFound)
		{
			return new InventorBridgeException(
				BridgeErrorCodes.UnknownOperation,
				$"The Inventor add-in does not know the operation '{operation}', so it is older than this server. Tell the user " +
				"to close Inventor, deploy the add-in again (see the README), and start Inventor.");
		}

		return new InventorBridgeException(
			exception.Data[BridgeResult.CodeMember] as string ?? BridgeErrorCodes.Internal,
			exception.Data[BridgeResult.MessageMember] as string ?? exception.Message,
			exception.Data[BridgeResult.DetailMember] as string);
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
			if (++watch.ChecksWithoutDialog < ChecksBeforeBlockWithoutDialog)
				return;

			LogBlockedWithoutDialog(processId);

			throw new InventorBridgeException(
				BridgeErrorCodes.BlockedByDialog,
				"Inventor is blocked: its main window is disabled, but it has no dialog with a title. A window of a " +
				"different process (ex. Vault or a licence service) can cause this. Ask the user to look at the screen.");
		}

		DialogSettings settings = _dialogSettings();

		foreach (string problem in settings.Problems)
			LogDialogSettingsProblem(problem);

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

			(string action, string telemetryAction) = outcome.Status switch
			{
				// The model must know what a click changed, ex. files saved in a format that older releases cannot open.
				ClickStatus.Clicked => ($"closed with {button}{DialogPolicy.ClickConsequence(DialogPolicy.Classify(dialog), button)}", TelemetryTags.DialogClicked),
				ClickStatus.DialogClosed => ("closed by a different server or the user", TelemetryTags.DialogClosedElsewhere),
				_ => ($"left open: {outcome.Message}", TelemetryTags.DialogClickFailed)
			};

			Report(processId, dialog, action, telemetryAction);

			if (outcome.Status is ClickStatus.Clicked)
				WriteClickAudit(dialog, button);
			else if (outcome.Status is ClickStatus.Refused or ClickStatus.Failed)
				needPerson.Add(dialog);
		}

		if (needPerson.Count == 0)
			return;

		foreach (DialogSnapshot dialog in needPerson)
			Report(processId, dialog, "left open for a person", TelemetryTags.DialogNeedsPerson);

		throw BlockedByDialog(needPerson[0]);
	}

	private void Report(int processId, DialogSnapshot dialog, string action, string telemetryAction)
	{
		string? type = DialogPolicy.Classify(dialog);

		// The text is gone when the dialog closes, and iLogic does not show the same error again for 120 minutes.
		LogBlockingDialog(
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
		ServerTelemetry.RecordDialog(type, telemetryAction);
	}

	private static InventorBridgeException BlockedByDialog(DialogSnapshot dialog)
	{
		string? type = DialogPolicy.Classify(dialog);
		string text = dialog.Text.Length > MaxTextInMessage ? dialog.Text[..MaxTextInMessage] + " ..." : dialog.Text;

		string message =
			"Inventor is blocked by a modal dialog. The call cannot run until the dialog is closed." + Environment.NewLine +
			$"Dialog: '{dialog.Title}' ({dialog.Framework ?? "not read"}, {type ?? "not in the catalog"})" + Environment.NewLine +
			(dialog.ReadError ?? $"Text: {text}") + Environment.NewLine +
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
			LogAuditWriteFailed(exception);
		}
	}

	private async Task<McpClient> EnsureConnectedAsync(bool retrying, CancellationToken cancellationToken)
	{
		await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			return await ConnectAsync(retrying, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_ = _connectLock.Release();
		}
	}

	private async Task<McpClient> ConnectAsync(bool retrying, CancellationToken cancellationToken)
	{
		// A change of the release makes the open connection stale, even when it still works.
		if (HasLiveConnection && _connectedGeneration == _selection.Generation)
			return _client!;

		await CloseConnectionAsync().ConfigureAwait(false);

		if (_connectedGeneration != _selection.Generation)
		{
			_automaticRelease = null;
			_lockedProcess = null;
		}

		_connectedGeneration = _selection.Generation;

		PipeResolution resolution = _selection.Resolve(_lockedProcess, retrying);

		if (resolution.PipeName is not string pipeName)
		{
			// A NotRunning resolution with no message means that no pipe exists, so the machine is examined for the cause.
			throw new InventorBridgeException(
				resolution.ErrorCode!,
				resolution.Message ?? Diagnose(resolution.ReleaseYear ?? _automaticRelease, pipeExists: false).Message);
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
			await pipe.ConnectAsync((int)_connectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			await pipe.DisposeAsync().ConfigureAwait(false);

			// The same exception means a full pipe or no pipe, so the machine is examined now, after the wait: the pipe can
			// appear during it. Only a full pipe has its own code. The other causes keep NotRunning, because inventor_start
			// and inventor_session with waitSeconds poll on it while Inventor loads.
			bool pipeExists = _selection.PipeExists(pipeName);
			(string code, string message) = Diagnose(resolution.ReleaseYear, pipeExists);

			LogConnectTimedOut(pipeName, code);

			throw new InventorBridgeException(code, message);
		}
		catch (UnauthorizedAccessException exception)
		{
			// The ACL of the pipe refuses this account at once: the pipe of a different Windows user, or a server with a
			// network logon (ex. started through SSH or WinRM), which the add-in denies. A retry cannot help.
			await pipe.DisposeAsync().ConfigureAwait(false);
			LogPipeRefused(pipeName, exception.Message);

			throw new InventorBridgeException(
				BridgeErrorCodes.UntrustedHost,
				$"The MCP bridge pipe '{pipeName}' refused this Windows account. Either the pipe belongs to a different Windows " +
				"user, or this server runs with a network logon (ex. through SSH or WinRM), which the add-in refuses. Start the " +
				"MCP client in the desktop session of the user who runs Inventor. Tell the user. Do not retry.");
		}
		catch (OperationCanceledException)
		{
			await pipe.DisposeAsync().ConfigureAwait(false);

			throw;
		}

		// Every request, and every dialog read and click, goes to this process, so it must be this user's Inventor.
		// See PipeHost.
		// The name has the id of the process that created the pipe. A different id means that a program took the name.
		string? refusal = GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId)
			? resolution.ProcessId is int expected && expected != (int)processId
				? $"The name of the pipe has the process id {expected}, but the process that hosts it has the id {processId}."
				: _checkHost((int)processId)
			: "Windows did not give the process that hosts the pipe.";

		if (refusal is not null)
		{
			await pipe.DisposeAsync().ConfigureAwait(false);
			LogPipeRefused(pipeName, refusal);

			throw new InventorBridgeException(
				BridgeErrorCodes.UntrustedHost,
				$"The process that hosts the MCP bridge pipe '{pipeName}' is not this user's Inventor, so the server did not " +
				$"use it: {refusal} Tell the user. Do not retry until the user has closed that process.");
		}

		McpClient client;

		try
		{
			using CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			handshake.CancelAfter(_handshakeTimeout);

			client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe, NullLoggerFactory.Instance), _clientOptions, NullLoggerFactory.Instance, handshake.Token).ConfigureAwait(false);
		}
		catch (Exception exception) when (cancellationToken.IsCancellationRequested is false && exception is OperationCanceledException or IOException or McpException)
		{
			await pipe.DisposeAsync().ConfigureAwait(false);

			// An Inventor that closed during the handshake is not an outdated add-in.
			(string code, string message) = _processRuns((int)processId)
				? (BridgeErrorCodes.BridgeOutdated,
					$"The Inventor add-in on pipe '{pipeName}' did not complete the MCP handshake of bridge version {BridgeProtocol.Version}, " +
					"so it is a build from before the bridge used MCP. Tell the user to close Inventor, deploy the add-in again (see " +
					"the README), and start Inventor.")
				: Diagnose(resolution.ReleaseYear, pipeExists: false);

			LogHandshakeFailed(exception, pipeName, code);

			throw new InventorBridgeException(code, message);
		}

		_pipe = pipe;
		_client = client;
		ReleaseYear = resolution.ReleaseYear;

		if (_selection.Chosen is null)
			_automaticRelease = resolution.ReleaseYear;

		_lockedProcess = (int)processId;
		InventorProcessId = (int)processId;

		LogConnected(pipeName, InventorProcessId);

		return client;
	}

	/// <summary>
	/// 	Says why no bridge answered, and counts only the Inventor processes of the release that was asked for.
	/// </summary>
	/// <remarks>
	/// 	With no release known, the manifest check passes when any running process has one, because the release of a
	/// 	process can be unknown too.
	/// </remarks>
	private (string Code, string Message) Diagnose(int? releaseYear, bool pipeExists)
	{
		IReadOnlyList<RunningInventor> running = _findRunning(releaseYear);

		bool deployed = releaseYear is int year
			? _isAddInDeployed(year)
			: running.Any(process => process.ReleaseYear is not int processRelease || _isAddInDeployed(processRelease));

		ConnectionCause cause = ConnectionDiagnosis.Classify(pipeExists, running, deployed, _timeProvider.GetLocalNow().DateTime);

		return ConnectionDiagnosis.Describe(cause, releaseYear, running);
	}

	/// <summary>
	/// 	Gives whether the process with the ID still runs.
	/// </summary>
	/// <remarks>
	/// 	A process that cannot be read (ex. no access) counts as running, so the pipe is used as before the check.
	/// </remarks>
	internal static bool ProcessRuns(int processId)
	{
		try
		{
			using Process process = Process.GetProcessById(processId);

			return process.HasExited is false;
		}
		catch (ArgumentException)
		{
			// No process has the ID.
			return false;
		}
		catch (Win32Exception)
		{
			return true;
		}
	}

	private async Task CloseAsync()
	{
		await _connectLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

		try
		{
			await CloseConnectionAsync().ConfigureAwait(false);
		}
		finally
		{
			_ = _connectLock.Release();
		}
	}

	/// <summary>
	/// 	Ends the session and the pipe. The caller holds <see cref="_connectLock"/>.
	/// </summary>
	private async Task CloseConnectionAsync()
	{
		// Closing must succeed on a pipe whose Inventor has closed, or the reconnect after it never runs.
		try
		{
			if (_client is not null)
				await _client.DisposeAsync().ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
		{
			// The pipe is already broken, so the session has nothing to end.
		}

		try
		{
			if (_pipe is not null)
				await _pipe.DisposeAsync().ConfigureAwait(false);
		}
		catch (IOException)
		{
			// The pipe is already broken, and the reconnect opens a new one.
		}

		_client = null;
		_pipe = null;

		// The next connection may be a different Inventor release.
		ReleaseYear = null;
		InventorProcessId = null;
	}

	public async ValueTask DisposeAsync()
	{
		await CloseAsync().ConfigureAwait(false);
		_gate.Dispose();
		_connectLock.Dispose();
	}

	private static Func<DialogSettings> LoadOnce()
	{
		DialogSettings settings = DialogSettings.Load();

		return () => settings;
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
