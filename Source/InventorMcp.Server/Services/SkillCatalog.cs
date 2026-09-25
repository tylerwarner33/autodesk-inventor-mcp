using System.Reflection;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	The skills that travel in the server assembly, in the Agent Skills format.
/// </summary>
/// <remarks>
/// 	Each skill is a folder under <c>Source/InventorMcp.Server/Skills</c> with a <c>SKILL.md</c> that starts with
/// 	<c>name</c> and <c>description</c> frontmatter.
/// 	The files are embedded with their path, so the Skills over MCP extension can serve the same bytes later.
/// 	See <c>Docs/Research/Usage-Findings-And-Knowledge-Delivery.md</c>, "The Skills over MCP extension".
/// </remarks>
internal sealed class SkillCatalog
{
	/// <summary>
	/// 	The prefix of the resource name of each skill file, set by the server project file.
	/// </summary>
	public const string ResourcePrefix = "InventorMcp.Server.Skills/";

	/// <summary>
	/// 	The file that describes a skill.
	/// </summary>
	public const string SkillFile = "SKILL.md";

	private readonly Dictionary<string, Dictionary<string, string>> _files;

	/// <summary>
	/// 	Reads the skills from the server assembly.
	/// </summary>
	public SkillCatalog()
		: this(ReadEmbeddedFiles())
	{
	}

	/// <param name="files">
	/// 	Each file's text by its path, ex. <c>interop/SKILL.md</c>.
	/// </param>
	/// <exception cref="InvalidOperationException">
	/// 	A skill has no <c>SKILL.md</c>, no frontmatter, or a <c>name</c> that is not its folder name.
	/// </exception>
	public SkillCatalog(IReadOnlyDictionary<string, string> files)
	{
		_files = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

		foreach ((string path, string text) in files)
		{
			int slash = path.IndexOf('/', StringComparison.Ordinal);

			if (slash <= 0)
				throw new InvalidOperationException($"The skill file '{path}' is not in a skill folder.");

			string skill = path[..slash];

			if (_files.TryGetValue(skill, out Dictionary<string, string>? skillFiles) is false)
			{
				skillFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				_files[skill] = skillFiles;
			}

			skillFiles[path[(slash + 1)..]] = text;
		}

		List<SkillSummary> skills = [];

		foreach ((string folder, Dictionary<string, string> skillFiles) in _files)
		{
			if (skillFiles.TryGetValue(SkillFile, out string? skillText) is false)
				throw new InvalidOperationException($"The skill folder '{folder}' has no {SkillFile}.");

			Dictionary<string, string> frontmatter = ReadFrontmatter(skillText)
				?? throw new InvalidOperationException($"'{folder}/{SkillFile}' has no frontmatter.");

			if (frontmatter.TryGetValue("name", out string? name) is false || string.Equals(name, folder, StringComparison.Ordinal) is false)
				throw new InvalidOperationException($"The name in '{folder}/{SkillFile}' must be '{folder}'.");

			if (frontmatter.TryGetValue("description", out string? description) is false || string.IsNullOrWhiteSpace(description))
				throw new InvalidOperationException($"'{folder}/{SkillFile}' has no description.");

			skills.Add(new SkillSummary(name, description, [.. skillFiles.Keys.Order(StringComparer.Ordinal)]));
		}

		Skills = [.. skills.OrderBy(static skill => skill.Name, StringComparer.Ordinal)];
	}

	/// <summary>
	/// 	Every skill, by name.
	/// </summary>
	public IReadOnlyList<SkillSummary> Skills { get; }

	/// <summary>
	/// 	Reads one file of a skill.
	/// </summary>
	/// <param name="skill">
	/// 	The skill name.
	/// </param>
	/// <param name="file">
	/// 	The path of the file in the skill folder. Null reads <c>SKILL.md</c>.
	/// </param>
	/// <returns>
	/// 	The text, or null when the skill or the file does not exist.
	/// </returns>
	public string? Read(string skill, string? file = null) =>
		_files.TryGetValue(skill, out Dictionary<string, string>? skillFiles)
		&& skillFiles.TryGetValue(string.IsNullOrWhiteSpace(file) ? SkillFile : file.Replace('\\', '/'), out string? text)
			? text
			: null;

	/// <summary>
	/// 	Reads the <c>key: value</c> lines between the first two <c>---</c> lines.
	/// </summary>
	/// <returns>
	/// 	The values by key, or null when the text does not start with frontmatter.
	/// </returns>
	internal static Dictionary<string, string>? ReadFrontmatter(string text)
	{
		string[] lines = text.ReplaceLineEndings("\n").Split('\n');

		if (lines.Length == 0 || lines[0].Trim() != "---")
			return null;

		Dictionary<string, string> values = new(StringComparer.Ordinal);

		for (int index = 1; index < lines.Length; index++)
		{
			if (lines[index].Trim() == "---")
				return values;

			int colon = lines[index].IndexOf(':', StringComparison.Ordinal);

			if (colon > 0)
				values[lines[index][..colon].Trim()] = lines[index][(colon + 1)..].Trim();
		}

		return null;
	}

	private static Dictionary<string, string> ReadEmbeddedFiles()
	{
		Assembly assembly = typeof(SkillCatalog).Assembly;
		Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);

		foreach (string resource in assembly.GetManifestResourceNames().Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
		{
			using Stream stream = assembly.GetManifestResourceStream(resource)!;
			using StreamReader reader = new(stream);

			// MSBuild writes the folder part of the name with a backslash.
			files[resource[ResourcePrefix.Length..].Replace('\\', '/')] = reader.ReadToEnd();
		}

		return files;
	}
}

/// <summary>
/// 	What a client needs to choose a skill.
/// </summary>
/// <param name="Name">
/// 	The skill name, the same as its folder name.
/// </param>
/// <param name="Description">
/// 	What the skill covers and when to read it.
/// </param>
/// <param name="Files">
/// 	The paths of its files, relative to its folder.
/// </param>
internal sealed record SkillSummary(string Name, string Description, IReadOnlyList<string> Files);
