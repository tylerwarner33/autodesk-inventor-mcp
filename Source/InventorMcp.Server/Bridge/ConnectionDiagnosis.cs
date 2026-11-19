using System.Globalization;

using InventorMcp.Contracts;
using InventorMcp.Server.Services;

namespace InventorMcp.Server.Bridge;

/// <summary>
/// 	Why the pipe of a release did not accept a connection.
/// </summary>
internal enum ConnectionCause
{
	/// <summary>
	/// 	The pipe exists, and every instance of it is in use.
	/// </summary>
	ConnectionsInUse,

	/// <summary>
	/// 	No Inventor of the release runs.
	/// </summary>
	NotRunning,

	/// <summary>
	/// 	Inventor runs, but no manifest of the add-in exists for the release.
	/// </summary>
	AddInNotDeployed,

	/// <summary>
	/// 	Inventor started a short time ago, so the add-in is probably loading.
	/// </summary>
	AddInLoading,

	/// <summary>
	/// 	Inventor runs for a long time, and the add-in has no pipe.
	/// </summary>
	AddInNotLoaded
}

/// <summary>
/// 	Tells the model why the bridge did not answer, so it takes the correct next step.
/// </summary>
/// <remarks>
/// 	A connect that times out gives the same exception for a pipe that is full and a pipe that does not exist.
/// 	So the machine is examined after the timeout, not before: the pipe can appear during the wait.
/// 	Only the full pipe has its own code. The other causes keep <see cref="BridgeErrorCodes.NotRunning"/>, because
/// 	<c>inventor_start</c> and <c>inventor_session</c> with <c>waitSeconds</c> poll on it.
/// 	See <c>Docs/Architecture.md</c>, "A named pipe rather than a local HTTP port".
/// </remarks>
internal static class ConnectionDiagnosis
{
	/// <summary>
	/// 	The time after the start of an Inventor in which a missing pipe means that the add-in is loading.
	/// </summary>
	/// <remarks>
	/// 	The measured starts took 34 s to 92 s. See <c>Docs/Research/Usage-Findings-And-Knowledge-Delivery.md</c>.
	/// </remarks>
	public static readonly TimeSpan LoadingWindow = TimeSpan.FromSeconds(120);

	/// <summary>
	/// 	Finds the cause from what the machine shows after a connect timeout.
	/// </summary>
	/// <param name="pipeExists">
	/// 	True when the pipe of the release is in the pipe list.
	/// </param>
	/// <param name="running">
	/// 	The Inventor processes of the release.
	/// </param>
	/// <param name="addInDeployed">
	/// 	True when a manifest of the add-in exists for the release.
	/// </param>
	/// <param name="now">
	/// 	The local time, which is the time zone of <see cref="RunningInventor.StartTime"/>.
	/// </param>
	/// <returns>
	/// 	The cause. A process with no start time counts as old.
	/// </returns>
	public static ConnectionCause Classify(bool pipeExists, IReadOnlyList<RunningInventor> running, bool addInDeployed, DateTime now)
	{
		if (pipeExists)
			return ConnectionCause.ConnectionsInUse;

		if (running.Count == 0)
			return ConnectionCause.NotRunning;

		if (addInDeployed is false)
			return ConnectionCause.AddInNotDeployed;

		return running.Any(process => process.StartTime is DateTime start && now - start < LoadingWindow)
			? ConnectionCause.AddInLoading
			: ConnectionCause.AddInNotLoaded;
	}

	/// <summary>
	/// 	Gives the error code and the message for a cause.
	/// </summary>
	/// <param name="cause">
	/// 	The cause.
	/// </param>
	/// <param name="releaseYear">
	/// 	The release that was asked for, or null when no release is known.
	/// </param>
	/// <param name="running">
	/// 	The Inventor processes of the release, for the hidden instance remark.
	/// </param>
	/// <returns>
	/// 	The code, and the message that tells the model what to do next.
	/// </returns>
	public static (string Code, string Message) Describe(ConnectionCause cause, int? releaseYear, IReadOnlyList<RunningInventor> running)
	{
		string subject = releaseYear is int year ? $"Autodesk Inventor {year}" : "Inventor";
		string hidden = running.Count > 0 && running.All(static process => process.HasMainWindow is false)
			? " It has no main window, so it is a hidden instance (ex. started through COM)."
			: string.Empty;

		return cause switch
		{
			ConnectionCause.ConnectionsInUse => (
				BridgeErrorCodes.ConnectionsInUse,
				$"{subject} hosts the MCP bridge, but every connection of the bridge is in use by other MCP clients. " +
				"Ask the user to close an MCP client that is not in use. Do not call inventor_start. " +
				"Do not wait: a wait cannot free a connection."),

			ConnectionCause.AddInNotDeployed => (
				BridgeErrorCodes.NotRunning,
				$"{subject} is running, but the Inventor MCP Bridge add-in is not deployed for this release, so no bridge " +
				"exists. Tell the user to deploy the add-in for this release (see the README), then to restart Inventor. " +
				$"Do not call inventor_start.{hidden}"),

			ConnectionCause.AddInLoading => (
				BridgeErrorCodes.NotRunning,
				$"{subject} is running and started less than {LoadingWindow.TotalSeconds:0} s ago, so the MCP bridge add-in " +
				$"is probably still loading. Do not call inventor_start. Wait with inventor_session and waitSeconds.{hidden}"),

			ConnectionCause.AddInNotLoaded => (
				BridgeErrorCodes.NotRunning,
				$"{subject} is running, but its MCP bridge add-in is not loaded: it started more than " +
				$"{LoadingWindow.TotalSeconds:0} s ago and has no bridge. Ask the user to check Tools > Add-Ins in Inventor, " +
				"and to read addin-startup.log and addin.log in " +
				$"%LOCALAPPDATA%\\InventorMcp\\{(releaseYear is int release ? release.ToString(CultureInfo.InvariantCulture) : "<year>")}. " +
				$"Do not call inventor_start.{hidden}"),

			_ => (
				BridgeErrorCodes.NotRunning,
				releaseYear is null
					? "No Inventor session is hosting the MCP bridge. Ask the user whether to start Inventor, then call " +
						"inventor_start. If Inventor is already open, make sure the Inventor MCP Bridge add-in is loaded."
					: $"{subject} is not hosting the MCP bridge. This session uses that release only, and does not fall back " +
						$"to another one. Ask the user whether to start it, then call inventor_start with version {releaseYear}. " +
						"Or call inventor_use_release to use a different release.")
		};
	}

	/// <summary>
	/// 	Says why an open pipe dropped, for the log.
	/// </summary>
	/// <param name="processRuns">
	/// 	True when the process that hosted the pipe still runs, or null when its id was not known.
	/// </param>
	/// <returns>
	/// 	The cause as text.
	/// </returns>
	public static string DescribeDrop(bool? processRuns) => processRuns switch
	{
		true => "the process still runs, so the add-in stopped serving (ex. it was unloaded) or the connection broke",
		false => "the process no longer runs, so Inventor closed or crashed",
		null => "the process of the connection was not known"
	};
}
