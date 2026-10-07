using InventorMcp.Server.Bridge;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The server uses a bridge pipe only when its host is an installed Inventor of this user, in this session.
/// </summary>
/// <remarks>
/// 	The test process stands in for the host, with its own executable as the installed Inventor.
/// </remarks>
[Trait("Level", "Unit")]
public sealed class PipeHostTests
{
	private static readonly string _ownExecutable = Path.GetFullPath(Environment.ProcessPath!);

	[Fact]
	public void ProcessOfThisUserRunningAnInstalledExecutableIsAccepted() =>
		Assert.Null(PipeHost.Check(Environment.ProcessId, [_ownExecutable]));

	[Fact]
	public void PathIsMatchedWithoutCase() =>
		Assert.Null(PipeHost.Check(Environment.ProcessId, [_ownExecutable.ToUpperInvariant()]));

	[Fact]
	public void ExecutableThatIsNotInstalledIsRefused() =>
		Assert.Contains("not an installed Inventor", PipeHost.Check(Environment.ProcessId, [@"C:\Program Files\Autodesk\Inventor 2025\Bin\Inventor.exe"]));

	[Fact]
	public void ProcessThatDoesNotExistIsRefused() =>
		Assert.NotNull(PipeHost.Check(int.MaxValue - 1, [_ownExecutable]));
}
