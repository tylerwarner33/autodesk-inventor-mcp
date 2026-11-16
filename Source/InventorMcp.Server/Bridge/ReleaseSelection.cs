using System.Diagnostics;
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
/// 	What the model must do next, or null when a pipe was found. Null with <see cref="BridgeErrorCodes.NotRunning"/> means
/// 	that the client examines the machine for the message.
/// </param>
/// <param name="ProcessId">
/// 	The id of the Inventor process that hosts the pipe, or null when no pipe was found.
/// </param>
internal sealed record PipeResolution(string? PipeName, int? ReleaseYear, string? ErrorCode, string? Message, int? ProcessId = null);

/// <summary>
/// 	The pipe of one Inventor process that hosts the bridge.
/// </summary>
/// <param name="PipeName">
/// 	The name in the pipe list.
/// </param>
/// <param name="ReleaseYear">
/// 	The release of the process, from the name.
/// </param>
/// <param name="ProcessId">
/// 	The id of the process, from the name.
/// </param>
internal sealed record PipeInstance(string PipeName, int ReleaseYear, int ProcessId);

/// <summary>
/// 	The Inventor release, and the Inventor process, that this server talks to.
/// </summary>
/// <remarks>
/// 	The order is: the process or release a tool chose, then <c>INVENTORMCP_RELEASE</c>, then the pipes that exist now.
/// 	A chosen release never falls back to another release, and a session never moves to another process unasked.
/// 	Each pipe name has the protocol version, the release and the process id, so the pipe list tells all three with no
/// 	connect. Each Claude session has its own server process, so one session cannot change the release of another.
/// 	See <c>Docs/Architecture.md</c>, "A named pipe rather than a local HTTP port".
/// </remarks>
internal sealed class ReleaseSelection
{
	/// <summary>
	/// 	The variable that fixes the release for a project, in the <c>env</c> of the MCP entry.
	/// </summary>
	public const string EnvironmentVariable = "INVENTORMCP_RELEASE";

	private const string PipeDirectory = @"\\.\pipe\";

	private readonly string _pipePrefix;
	private readonly string? _environmentValue;
	private readonly Func<IReadOnlyCollection<string>> _listPipes;
	private readonly Func<int, bool> _inSession;
	private int? _chosen;
	private int? _chosenProcess;

	/// <summary>
	/// 	Creates the selection.
	/// </summary>
	/// <param name="pipePrefix">
	/// 	The start of every bridge pipe name. Only a test passes it.
	/// </param>
	/// <param name="environmentValue">
	/// 	The value of <see cref="EnvironmentVariable"/>. Null reads the process environment.
	/// </param>
	/// <param name="listPipes">
	/// 	Lists the pipe names that exist. Null reads <c>\\.\pipe\</c>. Only a test passes it.
	/// </param>
	/// <param name="inSession">
	/// 	Tells if a process runs in the Windows session of this server. Null asks Windows. Only a test passes it.
	/// </param>
	public ReleaseSelection(
		string pipePrefix = BridgeProtocol.PipeNamePrefix,
		string? environmentValue = null,
		Func<IReadOnlyCollection<string>>? listPipes = null,
		Func<int, bool>? inSession = null)
	{
		_pipePrefix = pipePrefix;
		_environmentValue = environmentValue ?? Environment.GetEnvironmentVariable(EnvironmentVariable);
		_listPipes = listPipes ?? ListSystemPipes;
		_inSession = inSession ?? ProcessInSession;
	}

	/// <summary>
	/// 	Counts each change of the release or the process, so a client knows that its connection is stale.
	/// </summary>
	public int Generation { get; private set; }

	/// <summary>
	/// 	The release from a tool, or from the environment. Null when the selection is automatic.
	/// </summary>
	public int? Chosen => _chosen ?? EnvironmentRelease;

	/// <summary>
	/// 	The process that a tool chose, or null.
	/// </summary>
	public int? ChosenProcessId => _chosenProcess;

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
	/// 	Sets the release of this session, or clears it. A change also clears the chosen process.
	/// </summary>
	/// <param name="releaseYear">
	/// 	A supported release year, or null to go back to the environment or to automatic.
	/// </param>
	public void Choose(int? releaseYear)
	{
		int? before = Chosen;
		int? processBefore = _chosenProcess;

		_chosen = releaseYear;
		_chosenProcess = null;

		if (Chosen != before || processBefore is not null)
			Generation++;
	}

	/// <summary>
	/// 	Sets the release and the Inventor process of this session, ex. when two Inventor processes of one release run.
	/// </summary>
	/// <param name="releaseYear">
	/// 	The release of the process.
	/// </param>
	/// <param name="processId">
	/// 	The id of the process, from <see cref="RunningInstances"/>.
	/// </param>
	public void ChooseProcess(int releaseYear, int processId)
	{
		if (_chosen != releaseYear || _chosenProcess != processId)
			Generation++;

		_chosen = releaseYear;
		_chosenProcess = processId;
	}

	/// <summary>
	/// 	Lists the Inventor processes that host a bridge of this protocol version in the session of this server.
	/// </summary>
	/// <param name="releaseYear">
	/// 	Keeps only the processes of this release, or null for every release.
	/// </param>
	/// <returns>
	/// 	The processes, oldest release first, and the lowest process id first within a release.
	/// </returns>
	public IReadOnlyList<PipeInstance> RunningInstances(int? releaseYear = null) =>
		[.. _listPipes()
			.Select(name => BridgeProtocol.ParsePipeName(name, _pipePrefix, out int version, out int year, out int processId) is PipeNameKind.Instance
				&& version == BridgeProtocol.Version
				&& (releaseYear is null || year == releaseYear)
				&& _inSession(processId)
				? new PipeInstance(name, year, processId)
				: null)
			.OfType<PipeInstance>()
			.OrderBy(static instance => instance.ReleaseYear)
			.ThenBy(static instance => instance.ProcessId)];

	/// <summary>
	/// 	Lists the releases that have a bridge now, oldest first.
	/// </summary>
	/// <returns>
	/// 	The release years.
	/// </returns>
	public IReadOnlyList<int> RunningReleases() => [.. RunningInstances().Select(static instance => instance.ReleaseYear).Distinct()];

	/// <summary>
	/// 	Tells if a pipe exists now.
	/// </summary>
	/// <remarks>
	/// 	It reads the pipe list and opens no instance. A check that connects, ex. <see cref="File.Exists(string?)"/> on the
	/// 	pipe name, would use an instance of a pipe that can be full.
	/// </remarks>
	/// <param name="pipeName">
	/// 	The name of the pipe.
	/// </param>
	/// <returns>
	/// 	True when the name is in the pipe list.
	/// </returns>
	public bool PipeExists(string pipeName) => _listPipes().Contains(pipeName, StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// 	Picks the pipe to connect to.
	/// </summary>
	/// <param name="lockedProcessId">
	/// 	The process that this session connected to before, or null. A reconnect goes to this process only: the retry
	/// 	of a write after a dropped pipe must not reach a different Inventor.
	/// </param>
	/// <returns>
	/// 	The pipe, or the error that tells the model what to do.
	/// </returns>
	public PipeResolution Resolve(int? lockedProcessId = null)
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

		if (_chosenProcess is int chosenProcess)
		{
			return RunningInstances().FirstOrDefault(instance => instance.ProcessId == chosenProcess) is { } picked
				? Found(picked)
				: new PipeResolution(
					null,
					Chosen,
					BridgeErrorCodes.NotRunning,
					$"The Inventor process {chosenProcess}, which this session chose, does not host the MCP bridge now. " +
					"Call inventor_session to see the processes that do, then call inventor_use_release with a release and a processId. " +
					"Do not call inventor_start.");
		}

		int? year = Chosen;

		if (year is null)
		{
			IReadOnlyList<int> running = RunningReleases();

			if (running.Count > 1)
			{
				return new PipeResolution(
					null,
					null,
					BridgeErrorCodes.ReleaseRequired,
					$"More than one Inventor release hosts the MCP bridge ({string.Join(", ", running)}), and this session has not chosen one. " +
					"Ask the user which release to use, then call inventor_use_release with its year.");
			}

			year = running.Count == 1 ? running[0] : null;
		}

		IReadOnlyList<PipeInstance> instances = RunningInstances(year);

		if (instances.Count == 0)
			return Outdated(year) ?? new PipeResolution(null, year, BridgeErrorCodes.NotRunning, null);

		if (lockedProcessId is int locked && instances.FirstOrDefault(instance => instance.ProcessId == locked) is { } same)
			return Found(same);

		if (instances.Count == 1 && lockedProcessId is null)
			return Found(instances[0]);

		return new PipeResolution(
			null,
			year,
			BridgeErrorCodes.InstanceRequired,
			lockedProcessId is int gone
				? $"The Inventor process {gone}, which this session used, no longer hosts the MCP bridge, and this session does not " +
					$"move to another Inventor unasked, because the documents differ. Inventor {year} still runs in the process " +
					$"{string.Join(", ", instances.Select(static instance => instance.ProcessId))}. Ask the user which one to use, then " +
					$"call inventor_use_release with the release {year} and that processId, or start Inventor {year} again."
				: $"More than one Inventor {year} hosts the MCP bridge: the processes {string.Join(", ", instances.Select(static instance => instance.ProcessId))}. " +
					$"Ask the user which one to use, then call inventor_use_release with the release {year} and its processId. " +
					"Do not call inventor_start.");
	}

	private static PipeResolution Found(PipeInstance instance) => new(instance.PipeName, instance.ReleaseYear, null, null, instance.ProcessId);

	/// <summary>
	/// 	Finds a bridge of another protocol version, which this server does not use.
	/// </summary>
	/// <param name="year">
	/// 	The release that the session needs, or null for any release.
	/// </param>
	/// <returns>
	/// 	The error that tells the model what to do, or null when no such bridge exists.
	/// </returns>
	private PipeResolution? Outdated(int? year)
	{
		bool older = false;
		bool newer = false;

		foreach (string name in _listPipes())
		{
			PipeNameKind kind = BridgeProtocol.ParsePipeName(name, _pipePrefix, out int version, out int pipeYear, out _);
			bool wanted = year is null || pipeYear == year;

			// A protocol 1 pipe has no release in its name, so it counts for every release.
			older |= kind is PipeNameKind.Legacy || (kind is PipeNameKind.ReleasePipe && wanted);
			newer |= kind is PipeNameKind.Instance && version > BridgeProtocol.Version && wanted;
		}

		if (older)
		{
			return new PipeResolution(
				null,
				year,
				BridgeErrorCodes.BridgeOutdated,
				"An Inventor session hosts an outdated MCP bridge, which uses a pipe name of protocol version 2 or older. " +
				"Ask the user to close Inventor and redeploy the add-in for each release (see the README), then start Inventor again. " +
				"Do not call inventor_start.");
		}

		return newer
			? new PipeResolution(
				null,
				year,
				BridgeErrorCodes.VersionMismatch,
				"An Inventor session hosts an MCP bridge of a newer protocol version than this server. " +
				"Ask the user to restart the MCP client, so it starts the new server. Do not call inventor_start.")
			: null;
	}

	private static bool ProcessInSession(int processId)
	{
		try
		{
			using Process process = Process.GetProcessById(processId);

			return process.SessionId == Process.GetCurrentProcess().SessionId;
		}
		catch (ArgumentException)
		{
			// No process has this id, so its pipe is gone or about to be.
			return false;
		}
		catch (InvalidOperationException)
		{
			// The process ended while it was read.
			return false;
		}
	}

	private IReadOnlyCollection<string> ListSystemPipes()
	{
		try
		{
			return [.. Directory.EnumerateFiles(PipeDirectory, $"{_pipePrefix}*").Select(Path.GetFileName)!];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}
}
