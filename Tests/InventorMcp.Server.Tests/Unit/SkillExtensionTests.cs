using System.IO.Pipelines;
using System.Text;

using InventorMcp.Server.McpTools;
using InventorMcp.Server.Services;
using InventorMcp.Server.Telemetry;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Skills;
using ModelContextProtocol.Protocol;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The skills served through the Skills over MCP extension, read by the client API of the SDK.
/// </summary>
/// <remarks>
/// 	<c>ReadSkillResourceAsync</c> checks the size and the SHA-256 digest of each file against the manifest, so a read
/// 	that does not throw proves the manifest.
/// </remarks>
[Trait("Level", "Unit")]
public sealed class SkillExtensionTests
{
	private static readonly SkillCatalog _embedded = new();

	[Fact]
	public void EachSkillHasAPackageAtItsUri()
	{
		IReadOnlyList<McpServerSkillPackage> packages = _embedded.CreatePackages();

		Assert.Equal(
			_embedded.Skills.Select(static skill => SkillCatalog.SkillUri(skill.Name)),
			packages.Select(static package => package.Skill.Id));
		Assert.All(packages, static package => Assert.NotEmpty(package.Resources));
	}

	[Fact]
	public void TheSdkReadsTheSameNameAndDescriptionAsTheCatalog()
	{
		// Two readers read the same frontmatter: the catalog for inventor_skill, and the SDK for skills/list.
		IReadOnlyList<McpServerSkillPackage> packages = _embedded.CreatePackages();

		Assert.Equal(
			_embedded.Skills.Select(static skill => $"{skill.Name}: {skill.Description}"),
			packages.Select(static package => $"{package.Skill.ProtocolSkill?.Name}: {package.Skill.ProtocolSkill?.Description}"));
	}

	[Fact]
	public void ASkillFileWithAByteOrderMarkIsRefusedWithItsName()
	{
		byte[] content = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("---\nname: sample\ndescription: A sample skill.\n---\n\nBody")];

		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
			() => new SkillCatalog(new Dictionary<string, byte[]> { ["sample/SKILL.md"] = content }));

		Assert.Contains("sample/SKILL.md", exception.Message, StringComparison.Ordinal);
		Assert.Contains("byte order mark", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ServerStartsWithoutTheExtensionWhenTheSdkRefusesASkill()
	{
		// The catalog reads this frontmatter as text. The YAML reader of the SDK refuses the quote that does not end.
		SkillCatalog catalog = new(new Dictionary<string, string>
		{
			["sample/SKILL.md"] = "---\nname: sample\ndescription: \"A quote that does not end.\n---\n\nBody"
		});

		List<Exception> failures = [];

		await using SkillServer server = await SkillServer.StartAsync(catalog, failures.Add);

		_ = Assert.Single(failures);
		Assert.False(server.Client.ServerCapabilities.SupportsSkills());
		Assert.NotNull(InventorTool.Skill(catalog, "read", "sample", file: null).GetType().GetProperty("text"));
	}

	[Fact]
	public async Task ClientListsAndReadsEverySkillWithTheTextOfTheTool()
	{
		CancellationToken cancellationToken = TestContext.Current.CancellationToken;

		await using SkillServer server = await SkillServer.StartAsync(_embedded);

		Assert.True(server.Client.ServerCapabilities.SupportsSkills());

		IList<Skill> listed = await server.Client.ListSkillsAsync(cancellationToken: cancellationToken);

		Assert.Equal(
			_embedded.Skills.Select(static skill => SkillCatalog.SkillUri(skill.Name)).Order(StringComparer.Ordinal),
			listed.Select(static skill => skill.Uri).Order(StringComparer.Ordinal));

		foreach (SkillSummary summary in _embedded.Skills)
		{
			Skill skill = await server.Client.GetSkillAsync(SkillCatalog.SkillUri(summary.Name), cancellationToken: cancellationToken);
			ReadResourceResult result = await server.Client.ReadSkillResourceAsync(skill, skill.Uri, cancellationToken: cancellationToken);

			TextResourceContents text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
			Assert.Equal(_embedded.Read(summary.Name), text.Text);
			Assert.Equal(text.Text, ToolText(_embedded, summary.Name, file: null));
		}
	}

	[Fact]
	public async Task ClientReadsASupportingFile()
	{
		SkillCatalog catalog = new(new Dictionary<string, string>
		{
			["sample/SKILL.md"] = "---\nname: sample\ndescription: A sample skill for the test of the extension.\n---\n\nBody",
			["sample/extra/notes.md"] = "Notes"
		});

		await using SkillServer server = await SkillServer.StartAsync(catalog);

		Skill skill = await server.Client.GetSkillAsync(SkillCatalog.SkillUri("sample"), cancellationToken: TestContext.Current.CancellationToken);
		ReadResourceResult result = await server.Client.ReadSkillResourceAsync(skill, "skill://inventor/sample/extra/notes.md", cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal("Notes", Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text);
	}

	[Fact]
	public async Task ReadOfASkillFileCountsTheSkillAndAReadOfAnotherFileDoesNot()
	{
		using TelemetryCapture capture = new();

		// A name that only this test uses, because the meters are static and the tests run in parallel.
		string name = $"probe-{Guid.NewGuid():N}";
		SkillCatalog catalog = new(new Dictionary<string, string>
		{
			[$"{name}/SKILL.md"] = $"---\nname: {name}\ndescription: A skill for the test of the skill read count.\n---\n\nBody",
			[$"{name}/notes.md"] = "Notes"
		});

		await using (SkillServer server = await SkillServer.StartAsync(catalog))
		{
			Skill skill = await server.Client.GetSkillAsync(SkillCatalog.SkillUri(name), cancellationToken: TestContext.Current.CancellationToken);
			_ = await server.Client.ReadSkillResourceAsync(skill, skill.Uri, cancellationToken: TestContext.Current.CancellationToken);
			_ = await server.Client.ReadSkillResourceAsync(skill, $"skill://inventor/{name}/notes.md", cancellationToken: TestContext.Current.CancellationToken);
		}

		_ = Assert.Single(capture.Measurements("inventor.skill.reads"), measurement => measurement.Tag(TelemetryTags.SkillName) == name);
	}

	private static string? ToolText(SkillCatalog catalog, string name, string? file)
	{
		object result = InventorTool.Skill(catalog, "read", name, file);

		return result.GetType().GetProperty("text")?.GetValue(result) as string;
	}

	/// <summary>
	/// 	An MCP server with the skills of a catalog, and a client, in memory.
	/// </summary>
	private sealed class SkillServer(IHost host, McpClient client) : IAsyncDisposable
	{
		public McpClient Client { get; } = client;

		public static async Task<SkillServer> StartAsync(SkillCatalog catalog, Action<Exception>? onUnavailable = null)
		{
			Pipe toServer = new();
			Pipe toClient = new();

			HostApplicationBuilder builder = Host.CreateApplicationBuilder();
			_ = builder.Logging.ClearProviders();

			_ = builder.Services.AddMcpServer()
				.WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
				// The same as Program.cs.
				.WithInventorSkills(catalog, onUnavailable);

			IHost host = builder.Build();
			await host.StartAsync(TestContext.Current.CancellationToken);

			McpClient client = await McpClient.CreateAsync(
				new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
				cancellationToken: TestContext.Current.CancellationToken);

			return new SkillServer(host, client);
		}

		public async ValueTask DisposeAsync()
		{
			await Client.DisposeAsync();
			await host.StopAsync(TestContext.Current.CancellationToken);
			host.Dispose();
		}
	}
}
