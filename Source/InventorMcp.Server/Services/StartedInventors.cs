using System.ComponentModel;
using System.Diagnostics;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	The Inventor processes that this server started, the only ones that <c>inventor_stop</c> may close.
/// </summary>
/// <remarks>
/// 	Each entry keeps the start time of the process, because Windows can give the id of a process that ended to a new
/// 	process. An id with a different start time is not the process that this server started.
/// 	The record lives as long as the server process. A restart of the MCP client forgets it, so the user closes those
/// 	Inventors.
/// 	See <c>Docs/Architecture.md</c>, "The server closes only an Inventor that it started".
/// </remarks>
/// <param name="readStartTime">
/// 	Reads the start time of a process, or null when no process has the id. Null asks Windows. Only a test passes it.
/// </param>
internal sealed class StartedInventors(Func<int, DateTime?>? readStartTime = null)
{
	private readonly Func<int, DateTime?> _readStartTime = readStartTime ?? ReadStartTime;
	private readonly Dictionary<int, DateTime> _started = [];
	private readonly Lock _gate = new();

	/// <summary>
	/// 	Records a process that this server started.
	/// </summary>
	/// <param name="processId">
	/// 	The id of the process.
	/// </param>
	public void Add(int processId)
	{
		if (_readStartTime(processId) is not DateTime startTime)
			return;

		lock (_gate)
			_started[processId] = startTime;
	}

	/// <summary>
	/// 	Tells if this server started the process that has the id now.
	/// </summary>
	/// <param name="processId">
	/// 	The id of the process.
	/// </param>
	/// <returns>
	/// 	True when the id is recorded and the process that has it now has the recorded start time.
	/// </returns>
	public bool Contains(int processId)
	{
		DateTime recorded;

		lock (_gate)
		{
			if (_started.TryGetValue(processId, out recorded) is false)
				return false;
		}

		return _readStartTime(processId) == recorded;
	}

	/// <summary>
	/// 	Lists the processes that this server started and that still run.
	/// </summary>
	/// <returns>
	/// 	The process ids, lowest first.
	/// </returns>
	public IReadOnlyList<int> Running()
	{
		int[] recorded;

		lock (_gate)
			recorded = [.. _started.Keys];

		return [.. recorded.Where(Contains).Order()];
	}

	private static DateTime? ReadStartTime(int processId)
	{
		try
		{
			using Process process = Process.GetProcessById(processId);

			return process.StartTime;
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
		{
			// The process ended, or this account cannot read it. Either way it is not a process that this server can close.
			return null;
		}
	}
}
