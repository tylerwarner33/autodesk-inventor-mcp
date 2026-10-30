using System.Reflection;
using System.Text.RegularExpressions;

using InventorMcp.Contracts;
using InventorMcp.Server.Telemetry;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	Every tag that the server sets is a constant of <see cref="TelemetryTags"/>, and is on the allowlist.
/// </summary>
[Trait("Level", "Unit")]
public sealed partial class TelemetryTagsTests
{
	/// <summary>
	/// 	The tag names: the constants with a dot. A tag value never has one.
	/// </summary>
	public static TheoryData<string> TagNames =>
		[.. typeof(TelemetryTags).GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(static constant => constant.IsLiteral && constant.FieldType == typeof(string))
			.Select(static constant => (string)constant.GetRawConstantValue()!)
			.Where(static value => value.Contains('.', StringComparison.Ordinal))];

	[Theory]
	[MemberData(nameof(TagNames))]
	public void EveryTagNameIsAllowed(string name) => Assert.True(TelemetryTags.Allowed.Contains(name), $"'{name}' is not on the allowlist.");

	[Fact]
	public void ServerSetsNoTagWithALiteralName()
	{
		string source = Path.Combine(RepositoryRoot(), "Source", "InventorMcp.Server");
		List<string> literals = [];

		foreach (string file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
		{
			if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
				continue;

			string text = File.ReadAllText(file);

			foreach (Match match in LiteralTag().Matches(text))
				literals.Add($"{Path.GetFileName(file)}: {match.Value}");
		}

		Assert.Empty(literals);
	}

	[Fact]
	public void EveryErrorCodeThatAToolWritesIsKnown()
	{
		string tools = Path.Combine(RepositoryRoot(), "Source", "InventorMcp.Server", "McpTools");
		List<string> unknown = [];

		foreach (string file in Directory.EnumerateFiles(tools, "*.cs"))
		{
			foreach (Match match in ErrorCodeLiteral().Matches(File.ReadAllText(file)))
			{
				string code = match.Groups["code"].Value;

				if (TelemetryTags.ToolErrorValue(code) == TelemetryTags.Other)
					unknown.Add($"{Path.GetFileName(file)}: {code}");
			}
		}

		Assert.Empty(unknown);
	}

	[GeneratedRegex("""error = "(?<code>[a-z-]+)"\s*,""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
	private static partial Regex ErrorCodeLiteral();

	/// <summary>
	/// 	The Collector applies the same allowlist again, so a new tag must go in its config too.
	/// </summary>
	[Theory]
	[InlineData("powershell/otel-collector.yaml")]
	[InlineData("docker/otel-collector.yaml")]
	public void CollectorKeepsTheSameTagsAsTheServer(string config)
	{
		string text = File.ReadAllText(Path.Combine(RepositoryRoot(), "Tools", "Telemetry", config));

		Assert.Equal(
			TelemetryTags.Allowed.Order(StringComparer.Ordinal),
			KeptKeys(text, "span.attributes").Order(StringComparer.Ordinal));
		Assert.Equal(
			TelemetryTags.AllowedOnMetrics.Order(StringComparer.Ordinal),
			KeptKeys(text, "datapoint.attributes").Order(StringComparer.Ordinal));
		Assert.Empty(KeptKeys(text, "spanevent.attributes"));
	}

	/// <summary>
	/// 	The Collector checks the form of each name that the client can write. Each name that the server sends must pass.
	/// </summary>
	[Theory]
	[InlineData("powershell/otel-collector.yaml")]
	[InlineData("docker/otel-collector.yaml")]
	public void CollectorKeepsEveryNameThatTheServerSends(string config)
	{
		string text = File.ReadAllText(Path.Combine(RepositoryRoot(), "Tools", "Telemetry", config));
		string[] tools = [.. ServerToolNames(), TelemetryTags.Other];
		string[] methods = [.. ConstantValues(typeof(RequestMethods)), .. ConstantValues(typeof(NotificationMethods)), TelemetryTags.Other];
		string[] operations = [.. ConstantValues(typeof(BridgeOperations)), TelemetryTags.Other];

		Assert.NotEmpty(ServerToolNames());

		foreach (string target in new[] { "span", "datapoint" })
		{
			Regex tool = CollectorPattern(text, $"""{target}.attributes["{TelemetryTags.ToolName}"]""");
			Regex method = CollectorPattern(text, $"""{target}.attributes["{TelemetryTags.MethodName}"]""");

			Assert.All(tools, name => Assert.Matches(tool, name));
			Assert.All(methods, name => Assert.Matches(method, name));
			Assert.DoesNotMatch(tool, @"C:\Clients\Secret.ipt");
			Assert.DoesNotMatch(method, @"C:\Clients\Secret.ipt");
		}

		Regex spanName = CollectorPattern(text, "span.name");
		string[] spanNames =
		[
			.. methods,
			.. tools.Select(static name => $"{RequestMethods.ToolsCall} {name}"),
			.. operations.Select(static name => $"bridge {name}")
		];

		Assert.All(spanNames, name => Assert.Matches(spanName, name));
		Assert.DoesNotMatch(spanName, $@"{RequestMethods.ToolsCall} C:\Clients\Secret.ipt");
		Assert.DoesNotMatch(spanName, $"{RequestMethods.PromptsGet} secret_prompt");
	}

	[Fact]
	public void UnknownMethodIsOther()
	{
		Assert.Equal(RequestMethods.ToolsCall, TelemetryTags.MethodNameValue(RequestMethods.ToolsCall));
		Assert.Equal(NotificationMethods.InitializedNotification, TelemetryTags.MethodNameValue(NotificationMethods.InitializedNotification));
		Assert.Equal(TelemetryTags.Other, TelemetryTags.MethodNameValue(@"C:\Clients\Secret.ipt"));
		Assert.Equal(TelemetryTags.Other, TelemetryTags.MethodNameValue(null));
	}

	/// <summary>
	/// 	The names of the server's tools, from <see cref="McpServerToolAttribute.Name"/>.
	/// </summary>
	private static string[] ServerToolNames() =>
		[.. typeof(TelemetryTags).Assembly.GetTypes()
			.SelectMany(static type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
			.Select(static method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
			.OfType<string>()];

	private static string[] ConstantValues(Type type) =>
		[.. type.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(static field => field.IsLiteral && field.FieldType == typeof(string))
			.Select(static field => (string)field.GetRawConstantValue()!)];

	/// <summary>
	/// 	The pattern of the one <c>IsMatch</c> check on a path in the Collector config.
	/// </summary>
	private static Regex CollectorPattern(string config, string path)
	{
		Match check = Assert.Single(IsMatchCheck().Matches(config), match => match.Groups["path"].Value == path);

		return new Regex(check.Groups["pattern"].Value, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
	}

	[GeneratedRegex("""IsMatch\((?<path>[^,]+), "(?<pattern>[^"]+)"\)""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
	private static partial Regex IsMatchCheck();

	[Theory]
	[InlineData("System.InvalidOperationException", "System.InvalidOperationException")]
	[InlineData("System.Runtime.InteropServices.COMException", "System.Runtime.InteropServices.COMException")]
	[InlineData("Microsoft.CSharp.RuntimeBinder.RuntimeBinderException", "Microsoft.CSharp.RuntimeBinder.RuntimeBinderException")]
	[InlineData("CompilationError", TelemetryTags.CompilationError)]
	[InlineData("CompilationErrorException", TelemetryTags.CompilationError)]
	[InlineData(null, TelemetryTags.Other)]
	[InlineData("ClientName.Project.RuleException", TelemetryTags.Other)]
	[InlineData("Submission#0+ClientException", TelemetryTags.Other)]
	[InlineData("System.ServiceModel.FaultException`1[[ClientName.Detail, Client]]", TelemetryTags.Other)]
	public void ExceptionTypeKeepsOnlyTypesOfDotNetAndInventor(string? typeName, string expected) =>
		Assert.Equal(expected, TelemetryTags.ExceptionTypeValue(typeName));

	[Fact]
	public void BridgeValuesComeFromTheContracts()
	{
		Assert.Equal("inventor-busy", TelemetryTags.BridgeErrorValue("inventor-busy"));
		Assert.Equal(TelemetryTags.Other, TelemetryTags.BridgeErrorValue(@"C:\Clients\Secret.ipt"));
		Assert.Equal("run.csharp", TelemetryTags.BridgeOperationValue("run.csharp"));
		Assert.Equal(TelemetryTags.Other, TelemetryTags.BridgeOperationValue("made.up"));
	}

	[Fact]
	public void ClientNameIsCut()
	{
		Assert.Equal(TelemetryTags.None, TelemetryTags.ClientNameValue(null));
		Assert.Equal(TelemetryTags.MaxClientNameLength, TelemetryTags.ClientNameValue(new string('x', 200)).Length);
	}

	/// <summary>
	/// 	Finds the folder with the solution, above the test output.
	/// </summary>
	internal static string RepositoryRoot()
	{
		for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
		{
			if (File.Exists(Path.Combine(folder.FullName, "InventorMcp.slnx")))
				return folder.FullName;
		}

		throw new InvalidOperationException("The repository root was not found above the test output.");
	}

	/// <summary>
	/// 	The keys of the one <c>keep_keys</c> statement on a path.
	/// </summary>
	private static string[] KeptKeys(string config, string path)
	{
		Match statement = Assert.Single(KeepKeys().Matches(config), match => match.Groups["path"].Value == path);

		return [.. QuotedText().Matches(statement.Groups["keys"].Value).Select(static key => key.Groups["text"].Value)];
	}

	[GeneratedRegex("""keep_keys\((?<path>[a-z]+\.attributes), \[(?<keys>[^\]]*)\]\)""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
	private static partial Regex KeepKeys();

	[GeneratedRegex("\"(?<text>[^\"]+)\"", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
	private static partial Regex QuotedText();

	/// <summary>
	/// 	A tag name as a string literal: in <c>SetTag</c>, in a <c>KeyValuePair</c>, or in a <c>TagList</c> initializer.
	/// </summary>
	[GeneratedRegex("""(SetTag|AddTag|KeyValuePair<string, object\?>)\(\s*"|\{\s*"[a-z_]+\.[a-z_.]+"\s*,""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
	private static partial Regex LiteralTag();
}
