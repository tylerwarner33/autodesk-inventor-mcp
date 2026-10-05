using System.Globalization;

using InventorMcp.Contracts;

namespace InventorMcp.Server.Bridge;

/// <summary>
/// 	What <see cref="ReleaseSelection.Resolve"/> found.
/// </summary>
/// <param name="PipeName">
/// 	The pipe to connect to, or null when <paramref name="ErrorCode"/> is set.
/// </param>
/// <param name="ReleaseYear">
/// 	The release the pipe belongs to, or null when no release is known.
/// </param>
/// <param name="ErrorCode">
/// 	A value from <see cref="BridgeErrorCodes"/>, or null when a pipe was found.
/// </param>
/// <param name="Message">
/// 	What the model must do next, or null when a pipe was found.
/// </param>
internal sealed record PipeResolution(string? PipeName, int? ReleaseYear, string? ErrorCode, string? Message);

/// <summary>
/// 	The Inventor release this server talks to.
/// </summary>
/// <remarks>
/// 	The order is: the release a tool chose, then <c>INVENTORMCP_RELEASE</c>, then the pipes that exist now.
/// 	A chosen release never falls back to another release.
/// 	Each Claude session has its own server process, so one session cannot change the release of another.
/// </remarks>
internal sealed class ReleaseSelection
{
	/// <summary>
	/// 	The variable that fixes the release for a project, in the <c>env</c> of the MCP entry.
	/// </summary>
	public const string EnvironmentVariable = "INVENTORMCP_RELEASE";

	private const string _pipeDirectory = @"\\.\pipe\";

	private readonly string _pipePrefix;
	private readonly string? _environmentValue;
	private readonly Func<IReadOnlyCollection<string>> _listPipes;
	private int? _chosen;

	/// <summary>
	/// 	Creates the selection.
	/// </summary>
	/// <param name="pipePrefix">
	/// 	The start of every release pipe name. Only a test passes it.
	/// </param>
	/// <param name="environmentValue">
	/// 	The value of <see cref="EnvironmentVariable"/>. Null reads the process environment.
	/// </param>
	/// <param name="listPipes">
	/// 	Lists the pipe names that exist. Null reads <c>\\.\pipe\</c>. Only a test passes it.
	/// </param>
	public ReleaseSelection(
		string pipePrefix = BridgeProtocol.PipeNamePrefix,
		string? environmentValue = null,
		Func<IReadOnlyCollection<string>>? listPipes = null)
	{
		_pipePrefix = pipePrefix;
		_environmentValue = environmentValue ?? Environment.GetEnvironmentVariable(EnvironmentVariable);
		_listPipes = listPipes ?? ListSystemPipes;
	}

	/// <summary>
	/// 	Counts each change of the release, so a client knows that its connection is stale.
	/// </summary>
	public int Generation { get; private set; }

	/// <summary>
	/// 	The release from a tool, or from the environment. Null when the selection is automatic.
	/// </summary>
	public int? Chosen => _chosen ?? EnvironmentRelease;

	/// <summary>
	/// 	Where <see cref="Chosen"/> comes from: <c>chosen</c>, <c>environment</c> or <c>automatic</c>.
	/// </summary>
	public string Source => _chosen is not null ? "chosen" : EnvironmentRelease is not null ? "environment" : "automatic";

	private int? EnvironmentRelease =>
		int.TryParse(_environmentValue, NumberStyles.None, CultureInfo.InvariantCulture, out int year)
			&& InventorReleases.TryGetSoftwareVersion(year, out _)
			? year
			: null;

	/// <summary>
	/// 	Sets the release of this session, or clears it.
	/// </summary>
	/// <param name="releaseYear">
	/// 	A supported release year, or null to go back to the environment or to automatic.
	/// </param>
	public void Choose(int? releaseYear)
	{
		int? before = Chosen;

		_chosen = releaseYear;

		if (Chosen != before)
			Generation++;
	}

	/// <summary>
	/// 	Lists the releases whose pipe exists now, oldest first.
	/// </summary>
	/// <returns>
	/// 	The release years.
	/// </returns>
	public IReadOnlyList<int> RunningReleases()
	{
		IReadOnlyCollection<string> pipes = _listPipes();

		return [.. InventorReleases.Years.Where(year => pipes.Contains(PipeNameFor(year), StringComparer.OrdinalIgnoreCase))];
	}

	/// <summary>
	/// 	Picks the pipe to connect to.
	/// </summary>
	/// <returns>
	/// 	The pipe, or the error that tells the model what to do.
	/// </returns>
	public PipeResolution Resolve()
	{
		if (_chosen is null && string.IsNullOrWhiteSpace(_environmentValue) is false && EnvironmentRelease is null)
		{
			return new PipeResolution(
				null,
				null,
				BridgeErrorCodes.ReleaseRequired,
				$"The variable {EnvironmentVariable} holds '{_environmentValue}', which is not a supported release " +
				$"({string.Join(", ", InventorReleases.Years)}). Ask the user to fix the MCP entry, or call inventor_use_release.");
		}

		if (Chosen is int chosen)
			return new PipeResolution(PipeNameFor(chosen), chosen, null, null);

		IReadOnlyList<int> running = RunningReleases();

		if (running.Count == 1)
			return new PipeResolution(PipeNameFor(running[0]), running[0], null, null);

		if (running.Count > 1)
		{
			return new PipeResolution(
				null,
				null,
				BridgeErrorCodes.ReleaseRequired,
				$"More than one Inventor release hosts the MCP bridge ({string.Join(", ", running)}), and this session has not chosen one. " +
				"Ask the user which release to use, then call inventor_use_release with its year.");
		}

		if (_listPipes().Contains(_pipePrefix, StringComparer.OrdinalIgnoreCase))
		{
			return new PipeResolution(
				null,
				null,
				BridgeErrorCodes.BridgeOutdated,
				"An Inventor session hosts an outdated MCP bridge, which listens on a pipe with no release in its name. " +
				"Ask the user to close Inventor and redeploy the add-in for each release (see the README), then start Inventor again. " +
				"Do not call inventor_start.");
		}

		return new PipeResolution(
			null,
			null,
			BridgeErrorCodes.NotRunning,
			"No Inventor session is hosting the MCP bridge. Ask the user whether to start Inventor, then call " +
			"inventor_start. If Inventor is already open, make sure the Inventor MCP Bridge add-in is loaded.");
	}

	private string PipeNameFor(int releaseYear) => $"{_pipePrefix}.{releaseYear}";

	private IReadOnlyCollection<string> ListSystemPipes()
	{
		try
		{
			return [.. Directory.EnumerateFiles(_pipeDirectory, $"{_pipePrefix}*").Select(Path.GetFileName)!];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}
}
