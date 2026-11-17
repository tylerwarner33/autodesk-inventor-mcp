using System.ComponentModel;

using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

internal static partial class InventorTool
{
	[McpServerTool(Name = "inventor_use_release", Title = "Choose the Inventor release", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
	[Description("""
		Sets the Inventor release that this session talks to, ex. 2025. Several releases can run at the same time, each
		with its own bridge. Every other tool then reaches that release only, and never another one, even when its
		Inventor is closed. The choice holds until you change it, and it does not affect other Claude sessions.

		Call this when a tool returned release-required, or when the user asks to use a release. It returns the release
		that this session uses, whether its bridge answers now, the release it is connected to (the one running release
		when the choice is automatic), and the releases that run.

		A version of 0, or no version, clears the choice. The session then follows INVENTORMCP_RELEASE when it is set,
		and else the one release that runs.

		When more than one Inventor of one release runs, each has its own bridge, and a tool returns instance-required
		with the id, the start time and the window title of each process. Ask the user which one to use, then call this
		with the version and its processId. inventor_session lists the processes. The session then reaches that process
		only.
		""")]
	public static async Task<object> UseReleaseAsync(
		BridgeClient bridge,
		ReleaseSelection selection,
		[Description("Release year, ex. 2025. Use 0 or omit it to clear the choice.")] int? version = null,
		[Description("The id of the Inventor process to use, when more than one Inventor of the release runs. Needs a version.")] int? processId = null,
		CancellationToken cancellationToken = default)
	{
		if (processId is int process)
		{
			if (version is null or 0)
				return new { error = "invalid-arguments", message = "Give the version together with the processId.", runningInstances = Instances(selection) };

			int releaseOfProcess = version.Value;

			if (selection.RunningInstances(releaseOfProcess).Any(instance => instance.ProcessId == process) is false)
			{
				return new
				{
					error = BridgeErrorCodes.NotFound,
					message = $"No Inventor {releaseOfProcess} with the process id {process} hosts the MCP bridge. Nothing changed.",
					runningInstances = Instances(selection)
				};
			}

			selection.ChooseProcess(releaseOfProcess, process);

			return await DescribeChoiceAsync(bridge, selection, cancellationToken).ConfigureAwait(false);
		}

		if (version is int year and not 0 && InventorReleases.TryGetSoftwareVersion(year, out _) is false)
		{
			return new
			{
				error = "version-not-supported",
				message = $"{year} is not a supported release. Nothing changed.",
				supported = InventorReleases.Years
			};
		}

		selection.Choose(version is 0 ? null : version);

		return await DescribeChoiceAsync(bridge, selection, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<object> DescribeChoiceAsync(BridgeClient bridge, ReleaseSelection selection, CancellationToken cancellationToken)
	{
		bool bridgeAnswers = false;
		string? problem = null;

		try
		{
			bridgeAnswers = await bridge.TryConnectAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (InventorBridgeException exception)
		{
			// release-required and bridge-outdated are not a failure of this call. They tell the model what is left to do.
			problem = $"{exception.Code}: {exception.Message}";
		}

		return new
		{
			release = selection.Chosen,
			processId = selection.ChosenProcessId,
			selection = selection.Source,
			connectedRelease = bridge.ReleaseYear,
			connectedProcessId = bridge.InventorProcessId,
			bridgeAnswers,
			runningReleases = selection.RunningReleases(),
			runningInstances = Instances(selection),
			problem
		};
	}

	/// <summary>
	/// 	The Inventor processes that host a bridge, in the shape that each tool result uses.
	/// </summary>
	/// <param name="selection">
	/// 	The selection of this session.
	/// </param>
	/// <returns>
	/// 	The release, the process id and a description with the start time and the window title of each process.
	/// </returns>
	internal static object[] Instances(ReleaseSelection selection) =>
		[.. selection.RunningInstances().Select(instance => (object)new { release = instance.ReleaseYear, processId = instance.ProcessId, description = selection.Describe(instance) })];
}
