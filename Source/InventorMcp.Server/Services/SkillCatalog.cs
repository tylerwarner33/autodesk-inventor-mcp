using System.Reflection;
using System.Text;

using InventorMcp.Server.Telemetry;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Extensions.Skills;
using ModelContextProtocol.Protocol;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	The skills that travel in the server assembly, in the Agent Skills format.
/// </summary>
/// <remarks>
/// 	Each skill is a folder under <c>Source/InventorMcp.Server/Skills</c> with a <c>SKILL.md</c> that starts with
/// 	<c>name</c> and <c>description</c> frontmatter.
/// 	Two routes serve the same bytes: the tool <c>inventor_skill</c>, and the Skills over MCP extension through
/// 	<see cref="CreatePackages"/>.
/// 	This class is the only code that uses the experimental Skills API of the SDK, so a change of that API changes one
/// 	file.
/// 	See <c>Docs/Architecture.md</c>, "Longer knowledge travels as skills, and a tool description names its skill".
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

	/// <summary>
	/// 	The start of the URI of each skill in the Skills over MCP extension.
	/// </summary>
	/// <remarks>
	/// 	The prefix keeps the skill names apart from the skills of other servers in the same client.
	/// </remarks>
	// The address that clients see in the protocol, not a path on a machine, so it must not change with the setup.
#pragma warning disable S1075
	public const string UriPrefix = "skill://inventor/";
#pragma warning restore S1075

	private readonly Dictionary<string, Dictionary<string, byte[]>> _files;

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
		: this(files.ToDictionary(static file => file.Key, static file => Encoding.UTF8.GetBytes(file.Value), StringComparer.OrdinalIgnoreCase))
	{
	}

	/// <param name="files">
	/// 	Each file's bytes by its path, ex. <c>interop/SKILL.md</c>.
	/// </param>
	/// <exception cref="InvalidOperationException">
	/// 	A skill has no <c>SKILL.md</c>, no frontmatter, or a <c>name</c> that is not its folder name.
	/// 	Or a <c>SKILL.md</c> starts with a byte order mark.
	/// </exception>
	public SkillCatalog(IReadOnlyDictionary<string, byte[]> files)
	{
		_files = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);

		foreach ((string path, byte[] content) in files)
		{
			string normalized = path.Replace('\\', '/');
			int slash = normalized.IndexOf('/', StringComparison.Ordinal);

			if (slash <= 0)
				throw new InvalidOperationException($"The skill file '{path}' is not in a skill folder.");

			string skill = normalized[..slash];

			if (_files.TryGetValue(skill, out Dictionary<string, byte[]>? skillFiles) is false)
			{
				skillFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
				_files[skill] = skillFiles;
			}

			skillFiles[normalized[(slash + 1)..]] = content;
		}

		List<SkillSummary> skills = [];

		foreach ((string folder, Dictionary<string, byte[]> skillFiles) in _files)
		{
			if (skillFiles.TryGetValue(SkillFile, out byte[]? skillContent) is false)
				throw new InvalidOperationException($"The skill folder '{folder}' has no {SkillFile}.");

			// The bytes go to the client as they are, and a byte order mark before the first --- hides the frontmatter.
			if (skillContent.AsSpan().StartsWith(Encoding.UTF8.Preamble))
				throw new InvalidOperationException($"'{folder}/{SkillFile}' starts with a byte order mark. Save it as UTF-8 without one.");

			Dictionary<string, string> frontmatter = ReadFrontmatter(Encoding.UTF8.GetString(skillContent))
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
	/// 	Gives the URI of the <c>SKILL.md</c> of a skill in the Skills over MCP extension.
	/// </summary>
	/// <param name="skill">
	/// 	The skill name.
	/// </param>
	/// <returns>
	/// 	The URI, ex. <c>skill://inventor/interop/SKILL.md</c>.
	/// </returns>
	public static string SkillUri(string skill) => $"{UriPrefix}{skill}/{SkillFile}";

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
		_files.TryGetValue(skill, out Dictionary<string, byte[]>? skillFiles)
		&& skillFiles.TryGetValue(string.IsNullOrWhiteSpace(file) ? SkillFile : file.Replace('\\', '/'), out byte[]? content)
			? Encoding.UTF8.GetString(content)
			: null;

	/// <summary>
	/// 	Finds the skill whose <c>SKILL.md</c> has a URI.
	/// </summary>
	/// <param name="uri">
	/// 	The URI that a client read.
	/// </param>
	/// <returns>
	/// 	The name from the catalog, or null when the URI is not the <c>SKILL.md</c> of a skill.
	/// </returns>
	public string? FindSkillOfSkillFile(string? uri) =>
		Skills.FirstOrDefault(skill => string.Equals(SkillUri(skill.Name), uri, StringComparison.Ordinal))?.Name;

	/// <summary>
	/// 	Makes one package of the Skills over MCP extension for each skill.
	/// </summary>
	/// <remarks>
	/// 	The SDK computes the manifest digests from the same bytes that <c>inventor_skill</c> reads.
	/// </remarks>
	/// <returns>
	/// 	The packages, in the order of <see cref="Skills"/>.
	/// </returns>
	/// <exception cref="ArgumentException">
	/// 	The SDK does not accept the files of a skill, ex. frontmatter that its YAML reader does not support.
	/// </exception>
	public IReadOnlyList<McpServerSkillPackage> CreatePackages() =>
		[.. Skills.Select(skill => McpServerSkillPackage.Create(
			_files[skill.Name]
				.OrderBy(static file => file.Key, StringComparer.Ordinal)
				.Select(static file => new McpServerSkillFile { Path = file.Key, Content = file.Value }),
			new McpServerSkillPackageCreateOptions { Uri = SkillUri(skill.Name) }))];

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

	private static Dictionary<string, byte[]> ReadEmbeddedFiles()
	{
		Assembly assembly = typeof(SkillCatalog).Assembly;
		Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);

		foreach (string resource in assembly.GetManifestResourceNames().Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
		{
			using Stream stream = assembly.GetManifestResourceStream(resource)!;
			using MemoryStream content = new();
			stream.CopyTo(content);

			// MSBuild writes the folder part of the name with a backslash.
			files[resource[ResourcePrefix.Length..].Replace('\\', '/')] = content.ToArray();
		}

		return files;
	}
}

/// <summary>
/// 	Registers the skills of a <see cref="SkillCatalog"/> with the Skills over MCP extension.
/// </summary>
internal static class SkillCatalogRegistration
{
	/// <summary>
	/// 	Serves each skill through <c>skills/list</c>, <c>skills/get</c> and <c>resources/read</c>, and counts each
	/// 	read of a <c>SKILL.md</c> as a skill read, as <c>inventor_skill</c> does.
	/// </summary>
	/// <param name="builder">
	/// 	The MCP server builder.
	/// </param>
	/// <remarks>
	/// 	The extension is experimental, and <c>inventor_skill</c> serves the same skills.
	/// 	So when the SDK does not accept the skills, the server starts without the extension and keeps its tools.
	/// </remarks>
	/// <param name="catalog">
	/// 	The catalog that the server also gives to <c>inventor_skill</c>.
	/// </param>
	/// <param name="onUnavailable">
	/// 	Called with the failure when the SDK does not accept the skills.
	/// </param>
	/// <returns>
	/// 	The same builder.
	/// </returns>
	public static IMcpServerBuilder WithInventorSkills(
		this IMcpServerBuilder builder,
		SkillCatalog catalog,
		Action<Exception>? onUnavailable = null)
	{
		IReadOnlyList<McpServerSkillPackage> packages;

		try
		{
			packages = catalog.CreatePackages();
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
		{
			onUnavailable?.Invoke(exception);

			return builder;
		}

		return builder
			.WithSkillPackages(packages)
			.WithRequestFilters(filters => filters.AddReadResourceFilter(next => async (context, cancellationToken) =>
			{
				ReadResourceResult result = await next(context, cancellationToken).ConfigureAwait(false);

				// The name from the catalog, never the raw URI, which a client can fill with any text.
				if (catalog.FindSkillOfSkillFile(context.Params?.Uri) is string skill)
					ServerTelemetry.RecordSkillRead(skill);

				return result;
			}));
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
