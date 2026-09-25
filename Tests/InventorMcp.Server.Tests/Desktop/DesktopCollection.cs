namespace InventorMcp.Server.Tests.Desktop;

/// <summary>
/// 	The desktop and live tests share one desktop and one Inventor, so they run one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopCollection
{
	public const string Name = "Desktop";
}