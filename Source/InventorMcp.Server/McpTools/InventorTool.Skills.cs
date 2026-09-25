using System.ComponentModel;

using InventorMcp.Contracts;
using InventorMcp.Server.Services;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

/// <remarks>
/// 	Serves the embedded skills to every client through a tool, until the clients support the Skills over MCP
/// 	extension. See <c>Docs/Tasks/Usage-Findings-Implementation-Plan.md</c>, decision 7.
/// </remarks>
internal static partial class InventorTool
{
	[McpServerTool(Name = "inventor_skill", ReadOnly = true, Idempotent = true)]
	[Description("""
		Lists or reads the guides for this server, in the Agent Skills format. Each skill is short and covers one topic
		or one workflow, ex. the API traps for snippets, iLogic, drawings, projects and files, or a safe template edit.
		Call it with action 'list' to see each skill's description, then 'read' the skills that fit the task before you
		write a snippet or start the work. Works with Inventor closed.
		""")]
	public static object Skill(
		SkillCatalog skills,
		[Description("'list' for the name, description and files of each skill, or 'read' for one file of one skill.")] string action = "list",
		[Description("The skill to read, ex. 'interop'.")] string? name = null,
		[Description("The file to read in the skill folder. Default SKILL.md.")] string? file = null)
	{
		if (string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
			return new { skills = skills.Skills };

		if (string.Equals(action, "read", StringComparison.OrdinalIgnoreCase) is false)
			return new { error = "invalid-arguments", message = $"Unknown action '{action}'. Use 'list' or 'read'." };

		if (string.IsNullOrWhiteSpace(name))
			return new { error = "invalid-arguments", message = "Give the name of the skill to read. Call with action 'list' for the names." };

		return skills.Read(name, file) is string text
			? new { name, file = file ?? SkillCatalog.SkillFile, text }
			: new
			{
				error = BridgeErrorCodes.NotFound,
				message = $"No skill '{name}' with a file '{file ?? SkillCatalog.SkillFile}'. Skills: {string.Join(", ", skills.Skills.Select(static skill => skill.Name))}."
			};
	}
}
