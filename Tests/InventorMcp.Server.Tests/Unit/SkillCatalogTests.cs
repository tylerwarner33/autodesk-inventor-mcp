using System.Reflection;
using System.Text.RegularExpressions;

using InventorMcp.Server.Services;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The skills embedded in the server, and the rules of the Agent Skills format.
/// </summary>
[Trait("Level", "Unit")]
public sealed partial class SkillCatalogTests
{
	private static readonly SkillCatalog _embedded = new();

	[Fact]
	public void EveryPlannedSkillIsEmbedded() =>
		Assert.Equal(
			[
				"drawings", "ilogic", "ilogic-rule-edit", "interop", "modeling", "plugin-loop-cycle", "probe-project-code",
				"projects-and-files", "read-back-after-failed-write", "safe-template-edit", "split-long-work"
			],
			_embedded.Skills.Select(static skill => skill.Name));

	[Fact]
	public void NamesAndDescriptionsKeepTheFormat()
	{
		foreach (SkillSummary skill in _embedded.Skills)
		{
			// Agent Skills: lower case letters, digits and hyphens, at most 64; a description of at most 1024.
			Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", skill.Name);
			Assert.InRange(skill.Name.Length, 1, 64);
			Assert.InRange(skill.Description.Length, 40, 1024);
			Assert.Contains(SkillCatalog.SkillFile, skill.Files);
		}
	}

	[Fact]
	public void EveryToolThatASkillNamesExists()
	{
		HashSet<string> tools = [.. typeof(SkillCatalog).Assembly.GetTypes()
			.SelectMany(static type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
			.Select(static method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
			.OfType<string>()];

		foreach (SkillSummary skill in _embedded.Skills)
		{
			foreach (Match match in ToolName().Matches(_embedded.Read(skill.Name)!))
				Assert.True(tools.Contains(match.Value), $"Skill '{skill.Name}' names '{match.Value}', which is not a tool.");
		}
	}

	[Fact]
	public void EverySkillThatASkillNamesExists()
	{
		HashSet<string> names = [.. _embedded.Skills.Select(static skill => skill.Name)];

		foreach (SkillSummary skill in _embedded.Skills)
		{
			foreach (Match match in SkillReference().Matches(_embedded.Read(skill.Name)!))
				Assert.True(names.Contains(match.Groups["name"].Value), $"Skill '{skill.Name}' names the skill '{match.Groups["name"].Value}', which does not exist.");
		}
	}

	[Fact]
	public void ReadGivesSkillFileByDefault()
	{
		SkillCatalog catalog = new(new Dictionary<string, string>
		{
			["sample/SKILL.md"] = "---\nname: sample\ndescription: A sample skill for the test of the catalog.\n---\n\nBody",
			["sample/extra/notes.md"] = "Notes"
		});

		Assert.EndsWith("Body", catalog.Read("sample"));
		Assert.Equal("Notes", catalog.Read("sample", "extra\\notes.md"));
		Assert.Null(catalog.Read("sample", "missing.md"));
		Assert.Null(catalog.Read("missing"));
		Assert.Equal(["SKILL.md", "extra/notes.md"], Assert.Single(catalog.Skills).Files);
	}

	[Theory]
	[InlineData("---\nname: other\ndescription: A skill whose name is not its folder.\n---\n")]
	[InlineData("---\ndescription: A skill with no name.\n---\n")]
	[InlineData("---\nname: sample\n---\n")]
	[InlineData("name: sample\ndescription: No frontmatter lines.\n")]
	public void ASkillThatBreaksTheFormatIsRefused(string text) =>
		Assert.Throws<InvalidOperationException>(() => new SkillCatalog(new Dictionary<string, string> { ["sample/SKILL.md"] = text }));

	[Fact]
	public void ASkillFolderWithNoSkillFileIsRefused() =>
		Assert.Throws<InvalidOperationException>(() => new SkillCatalog(new Dictionary<string, string> { ["sample/notes.md"] = "Notes" }));

	[GeneratedRegex(@"\binventor_[a-z_]+[a-z]\b", RegexOptions.CultureInvariant)]
	private static partial Regex ToolName();

	/// <summary>
	/// 	The way a skill names another, ex. "the `ilogic` skill".
	/// </summary>
	[GeneratedRegex(@"the `(?<name>[a-z0-9-]+)` skill", RegexOptions.CultureInvariant)]
	private static partial Regex SkillReference();
}
