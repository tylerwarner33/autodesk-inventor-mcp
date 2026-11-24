using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The text that the server sends in the <c>initialize</c> response, which every session loads.
/// </summary>
[Trait("Level", "Unit")]
public sealed class ServerInstructionsTests
{
	/// <summary>
	/// 	The limit for the instructions, well below the cut of the client.
	/// </summary>
	/// <remarks>
	/// 	Claude Code keeps the first 4,096 characters of the instructions of a server and cuts the rest, so the rules at
	/// 	the end were lost. A rule that a model needs only with one tool goes in that tool's description or result.
	/// 	See <c>Docs/Architecture.md</c>, "The modelling rules travel in the initialize response".
	/// </remarks>
	private const int MaxCharacters = 2500;

	[Fact]
	public void TheInstructionsStayShort()
	{
		using Stream stream = typeof(SkillCatalog).Assembly.GetManifestResourceStream("InventorMcp.Server.ServerInstructions.md")!;
		using StreamReader reader = new(stream);
		string instructions = reader.ReadToEnd();

		Assert.True(instructions.Length <= MaxCharacters, $"The server instructions have {instructions.Length} characters, more than {MaxCharacters}. Move a rule that only one tool needs to that tool.");
	}
}
