using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The record of the Inventor processes that this server started, which inventor_stop may close.
/// </summary>
[Trait("Level", "Unit")]
public sealed class StartedInventorsTests
{
	private static readonly DateTime _started = new(2026, 11, 24, 14, 28, 0, DateTimeKind.Local);

	[Fact]
	public void AStartedProcessIsRecorded()
	{
		StartedInventors record = new(static _ => _started);

		record.Add(4242);

		Assert.True(record.Contains(4242));
		Assert.Equal([4242], record.Running());
	}

	[Fact]
	public void AProcessThatThisServerDidNotStartIsNotRecorded()
	{
		StartedInventors record = new(static _ => _started);

		record.Add(4242);

		Assert.False(record.Contains(4343));
	}

	/// <summary>
	/// 	Windows can give the id of a process that ended to a new process, which this server did not start.
	/// </summary>
	[Fact]
	public void AReusedProcessIdIsNotTheStartedProcess()
	{
		DateTime startTime = _started;
		StartedInventors record = new(_ => startTime);

		record.Add(4242);
		startTime = _started.AddMinutes(5);

		Assert.False(record.Contains(4242));
		Assert.Empty(record.Running());
	}

	[Fact]
	public void AProcessThatEndedIsNotRunning()
	{
		bool runs = true;
		StartedInventors record = new(_ => runs ? _started : null);

		record.Add(4242);
		runs = false;

		Assert.False(record.Contains(4242));
		Assert.Empty(record.Running());
	}

	[Fact]
	public void AProcessThatEndedBeforeTheRecordIsNotRecorded()
	{
		StartedInventors record = new(static _ => null);

		record.Add(4242);

		Assert.Empty(record.Running());
	}
}
