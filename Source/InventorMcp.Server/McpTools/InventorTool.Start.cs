using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

internal static partial class InventorTool
{
	/// <summary>
	/// 	The <c>requestState</c> of a call that asked the user to choose a release.
	/// </summary>
	private const string _chooseReleaseState = "choose-release";

	/// <summary>
	/// 	Key of the elicitation in <c>inputRequests</c>, and of the field inside its form.
	/// </summary>
	private const string _releaseField = "release";

	/// <summary>
	/// 	How long the start waits for the add-in to open the pipe.
	/// </summary>
	/// <remarks>
	/// 	Stays under what clients wait for one call. Progress resets a client's timeout only where the client supports it.
	/// </remarks>
	private static readonly TimeSpan _startWaitLimit = TimeSpan.FromSeconds(45);

	/// <summary>
	/// 	Pause between connection attempts. Each failed attempt also waits for the bridge's connect timeout.
	/// </summary>
	private static readonly TimeSpan _startPollInterval = TimeSpan.FromSeconds(2);

	[McpServerTool(Name = "inventor_start", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = false)]
	[Description("""
		Starts Autodesk Inventor when no session hosts the MCP bridge, and waits for the bridge add-in to load.

		Call this only when the user asks to start Inventor, or after you ask them and they agree. Never call it only
		because another tool returned inventor-not-running.

		If Inventor already hosts the bridge, nothing starts. If an Inventor process runs without the bridge, nothing
		starts either, and the result lists the processes. If more than one release has the add-in deployed and no
		version is given, the user is asked to choose. When the client cannot ask, the result is version-required with
		the release names: ask the user, then call again with version.

		On success, call inventor_session to confirm the session. Inventor can still be at a sign-in or recovery dialog.
		""")]
	public static async Task<object> Start(
		BridgeClient bridge,
		InventorInstallations installations,
		RequestContext<CallToolRequestParams> context,
		IProgress<ProgressNotificationValue> progress,
		[Description("Release year to start, ex. 2025. Omit to start the only release with the add-in deployed, or to let the user choose.")] int? version = null,
		CancellationToken cancellationToken = default)
	{
		// A retry after the form runs every check again, because the state can change while the form is open.
		try
		{
			Contracts.Models.SessionInfo session = await bridge
				.InvokeAsync<Contracts.Models.SessionInfo>(BridgeOperations.Session, null, cancellationToken)
				.ConfigureAwait(false);

			return new
			{
				status = "already-running",
				message = "An Inventor session already hosts the MCP bridge. Nothing was started.",
				session
			};
		}
		catch (InventorBridgeException exception) when (exception.Code == BridgeErrorCodes.NotRunning)
		{
		}
		catch (InventorBridgeException exception)
		{
			// Any other failure came from a session that is running, so starting a second Inventor cannot help.
			return new { error = exception.Code, message = exception.Message, detail = exception.Detail };
		}

		IReadOnlyList<RunningInventor> running = InventorInstallations.FindRunning();

		if (running.Count > 0)
		{
			return new
			{
				error = "inventor-starting-or-no-bridge",
				message = "Inventor is running, but it does not host the MCP bridge. Either it is still starting, or the " +
					"add-in did not load. Nothing was started, because a second Inventor never gets the bridge. A process with " +
					"no main window is a hidden instance, ex. one started through COM. See %LOCALAPPDATA%\\InventorMcp\\addin.log " +
					"and addin-startup.log.",
				processes = running
			};
		}

		IReadOnlyList<InventorRelease> installed = installations.FindInstalled();

		if (installed.Count == 0)
		{
			return new
			{
				error = "inventor-not-installed",
				message = "No supported Inventor release is installed. The add-in supports Autodesk Inventor 2025, 2026 and 2027."
			};
		}

		List<InventorRelease> deployed = [.. installed.Where(static release => release.AddInDeployed)];
		string[] notDeployed = [.. installed.Where(static release => release.AddInDeployed is false).Select(static release => release.DisplayName)];

		InventorRelease release;

		if (TryReadChosenRelease(context.Params, out ElicitResult? answer))
		{
			if (answer.IsAccepted is false)
				return new { status = "cancelled", message = "The user did not choose a release. Nothing was started." };

			if (ReadChosenYear(answer) is not int chosenYear || deployed.Find(candidate => candidate.Year == chosenYear) is not InventorRelease chosen)
			{
				return new
				{
					error = "version-not-offered",
					message = "The answer names a release that was not offered. Nothing was started.",
					releases = deployed.Select(static candidate => candidate.DisplayName)
				};
			}

			release = chosen;
		}
		else if (version is int year)
		{
			if (installed.FirstOrDefault(candidate => candidate.Year == year) is not InventorRelease named)
			{
				return new
				{
					error = "version-not-installed",
					message = $"Autodesk Inventor {year} is not installed. Nothing was started.",
					installed = installed.Select(static candidate => candidate.DisplayName)
				};
			}

			if (named.AddInDeployed is false)
				return AddInNotDeployed([named]);

			release = named;
		}
		else if (deployed.Count == 0)
		{
			return AddInNotDeployed(installed);
		}
		else if (deployed.Count == 1)
		{
			release = deployed[0];
		}
		else if (CanAskUser(context.Server))
		{
			throw new InputRequiredException(
				new Dictionary<string, InputRequest> { [_releaseField] = InputRequest.ForElicitation(ChooseReleaseForm(deployed)) },
				_chooseReleaseState);
		}
		else
		{
			return new
			{
				error = "version-required",
				message = "More than one Inventor release has the MCP bridge add-in, and this client cannot show a choice. " +
					"Ask the user which release to start, then call inventor_start again with version set to its year.",
				releases = deployed.Select(static candidate => candidate.DisplayName),
				notDeployed
			};
		}

		DetachedProcess process;

		try
		{
			process = DetachedProcess.Start(release.ExecutablePath);
		}
		catch (Win32Exception exception)
		{
			return new
			{
				error = "start-failed",
				message = $"{release.DisplayName} could not be started. Nothing is running.",
				detail = $"{exception.Message} (Win32 error {exception.NativeErrorCode})"
			};
		}

		using (process)
		{
			return await WaitForBridgeAsync(bridge, release, process, progress, cancellationToken).ConfigureAwait(false);
		}
	}

	private static async Task<object> WaitForBridgeAsync(
		BridgeClient bridge,
		InventorRelease release,
		DetachedProcess process,
		IProgress<ProgressNotificationValue> progress,
		CancellationToken cancellationToken)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();

		while (true)
		{
			progress.Report(new ProgressNotificationValue
			{
				Progress = (float)stopwatch.Elapsed.TotalSeconds,
				Total = (float)_startWaitLimit.TotalSeconds,
				Message = $"Waiting for {release.DisplayName} to load the MCP bridge add-in."
			});

			if (await bridge.TryConnectAsync(cancellationToken).ConfigureAwait(false))
			{
				return new
				{
					status = "started",
					release = release.DisplayName,
					processId = process.Id,
					message = "Inventor started and the MCP bridge add-in answered. Call inventor_session to confirm the session. " +
						"Inventor can still be finishing its start, or waiting for the user at a sign-in or recovery dialog."
				};
			}

			// A launcher that hands over to another Inventor process would look like an exit, so only an exit with no
			// Inventor left is a failure.
			if (process.HasExited && InventorInstallations.FindRunning().Count == 0)
			{
				return new
				{
					error = "inventor-exited",
					message = $"{release.DisplayName} started and then exited before the MCP bridge answered. " +
						"See %LOCALAPPDATA%\\InventorMcp\\addin-startup.log and addin.log.",
					exitCode = process.ExitCode
				};
			}

			if (stopwatch.Elapsed >= _startWaitLimit)
			{
				return new
				{
					status = "still-starting",
					release = release.DisplayName,
					processId = process.Id,
					message = $"{release.DisplayName} is running, but the MCP bridge did not answer within " +
						$"{_startWaitLimit.TotalSeconds:0} s. Inventor may be waiting for the user at a sign-in or recovery dialog. " +
						"Call inventor_session later. Do not call inventor_start again."
				};
			}

			await Task.Delay(_startPollInterval, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// 	True when the client can show a form for this request.
	/// </summary>
	/// <remarks>
	/// 	Must be the request's server. On 2026-07-28 the client declares its capabilities with each request, and the
	/// 	root server's are null.
	/// </remarks>
	private static bool CanAskUser(McpServer server) =>
		server.IsMrtrSupported
		&& server.ClientCapabilities?.Elicitation is { } elicitation
		// An empty elicitation capability means form mode. Only a client that declares URL mode alone cannot show a form.
		&& (elicitation.Form is not null || elicitation.Url is null);

	private static ElicitRequestParams ChooseReleaseForm(IReadOnlyList<InventorRelease> releases) => new()
	{
		Message = "More than one Inventor release can host the MCP bridge. Which release should start?",
		RequestedSchema = new ElicitRequestParams.RequestSchema
		{
			Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
			{
				[_releaseField] = new ElicitRequestParams.TitledSingleSelectEnumSchema
				{
					Title = "Inventor release",
					OneOf =
					[
						.. releases.Select(static release => new ElicitRequestParams.EnumSchemaOption
						{
							Const = release.Year.ToString(CultureInfo.InvariantCulture),
							Title = release.DisplayName
						})
					]
				}
			},
			Required = [_releaseField]
		}
	};

	/// <summary>
	/// 	Reads the user's answer when this call is the retry after the form.
	/// </summary>
	private static bool TryReadChosenRelease(CallToolRequestParams? parameters, [NotNullWhen(true)] out ElicitResult? answer)
	{
		answer = null;

		if (parameters?.RequestState != _chooseReleaseState
			|| parameters.InputResponses?.TryGetValue(_releaseField, out InputResponse? response) is not true)
		{
			return false;
		}

		answer = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);

		return answer is not null;
	}

	private static int? ReadChosenYear(ElicitResult answer)
	{
		if (answer.Content?.TryGetValue(_releaseField, out JsonElement value) is not true)
			return null;

		string? text = value.ValueKind switch
		{
			JsonValueKind.String => value.GetString(),
			JsonValueKind.Number => value.GetRawText(),
			_ => null
		};

		return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int year) ? year : null;
	}

	private static object AddInNotDeployed(IEnumerable<InventorRelease> releases) => new
	{
		error = "addin-not-deployed",
		message = "The MCP bridge add-in is not deployed for the release, so Inventor would start without the bridge. " +
			"Nothing was started. Build and deploy the add-in for the release first, with Inventor closed.",
		releases = releases.Select(static release => new
		{
			release = release.DisplayName,
			developmentDeploy = $"dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -p:AutodeskVersion={release.Year} -p:DeployAddIn=true",
			bundleDeploy = $"dotnet build Source/InventorMcp.AddIn/InventorMcp.AddIn.csproj -c Release -p:AutodeskVersion={release.Year} -p:DeployBundle=true"
		})
	};
}
