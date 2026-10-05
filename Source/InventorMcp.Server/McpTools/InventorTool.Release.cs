using System.ComponentModel;

using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

internal static partial class InventorTool
{
	[McpServerTool(Name = "inventor_use_release", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = false)]
	[Description("""
		Sets the Inventor release that this session talks to, ex. 2025. Several releases can run at the same time, each
		with its own bridge. Every other tool then reaches that release only, and never another one, even when its
		Inventor is closed. The choice holds until you change it, and it does not affect other Claude sessions.

		Call this when a tool returned release-required, or when the user asks to use a release. It returns the release
		that this session uses, whether its bridge answers now, the release it is connected to (the one running release
		when the choice is automatic), and the releases that run.

		A version of 0, or no version, clears the choice. The session then follows INVENTORMCP_RELEASE when it is set,
		and else the one release that runs.
		""")]
	public static async Task<object> UseRelease(
		BridgeClient bridge,
		ReleaseSelection selection,
		[Description("Release year, ex. 2025. Use 0 or omit it to clear the choice.")] int? version = null,
		CancellationToken cancellationToken = default)
	{
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
			selection = selection.Source,
			connectedRelease = bridge.ReleaseYear,
			bridgeAnswers,
			runningReleases = selection.RunningReleases(),
			problem
		};
	}
}
