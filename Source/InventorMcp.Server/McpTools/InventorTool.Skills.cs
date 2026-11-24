using System.ComponentModel;

using InventorMcp.Contracts;
using InventorMcp.Server.Services;
using InventorMcp.Server.Telemetry;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

/// <remarks>
/// 	Serves the embedded skills to every client through a tool.
/// 	The Skills over MCP extension serves the same bytes, but the clients of this server do not support it yet.
/// 	See <c>Docs/Architecture.md</c>, "Longer knowledge travels as skills, and a tool description names its skill".
/// </remarks>
internal static partial class InventorTool
{
	/// <summary>
	/// 	Where the Inventor API facts are, sent with each <c>list</c> result.
	/// </summary>
	/// <remarks>
	/// 	The client loads the plugin skills itself, so the server cannot list them. A model needs this text only when it
	/// 	works with the server skills, so it is here and not in the server instructions, which every session loads.
	/// 	<c>SkillCatalogTests</c> checks that it names each plugin skill and its plugin.
	/// </remarks>
	internal const string PluginSkills = """
		The Inventor API facts that make most snippets fail are not server skills. They are in the plugins `inventor-api`, `inventor-ilogic` and `inventor-data` of the marketplace `tylerwarner-autodesk`: the skills `inventor-api-traps` and `inventor-modeling` and `inventor-drawings` (`inventor-api` plugin), `inventor-ilogic` (`inventor-ilogic` plugin) and `inventor-projects-and-files` (`inventor-data` plugin). If your client has them, read `inventor-api-traps` before your first snippet, and the others before a task that they cover.
		""";

	[McpServerTool(Name = "inventor_skill", Title = "Read the server guides", ReadOnly = true, Idempotent = true, OpenWorld = false)]
	[Description("""
		Lists or reads the guides for this server, in the Agent Skills format. Each skill is short and covers one topic
		or one workflow, ex. the snippet host, the iLogic tools, drawing checks, file tools, or a safe template edit.
		Call it with action 'list' one time in a session to see each skill's description and where the Inventor API facts
		are. Then 'read' the skills that fit the task before you write a snippet or start the work. Works with Inventor
		closed.
		""")]
	public static object Skill(
		SkillCatalog skills,
		[Description("'list' for the name, description and files of each skill, or 'read' for one file of one skill.")] string action = "list",
		[Description("The skill to read, ex. 'snippets'.")] string? name = null,
		[Description("The file to read in the skill folder. Default SKILL.md.")] string? file = null)
	{
		if (string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
			return new { skills = skills.Skills, pluginSkills = PluginSkills };

		if (string.Equals(action, "read", StringComparison.OrdinalIgnoreCase) is false)
			return new { error = "invalid-arguments", message = $"Unknown action '{action}'. Use 'list' or 'read'." };

		if (string.IsNullOrWhiteSpace(name))
			return new { error = "invalid-arguments", message = "Give the name of the skill to read. Call with action 'list' for the names." };

		if (skills.Read(name, file) is string text)
		{
			// The name from the catalog, never the raw argument, which a model can fill with any text.
			ServerTelemetry.RecordSkillRead(skills.Skills.First(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase)).Name);

			return new { name, file = file ?? SkillCatalog.SkillFile, text };
		}

		return new
		{
			error = BridgeErrorCodes.NotFound,
			message = $"No skill '{name}' with a file '{file ?? SkillCatalog.SkillFile}'. Skills: {string.Join(", ", skills.Skills.Select(static skill => skill.Name))}."
		};
	}
}
