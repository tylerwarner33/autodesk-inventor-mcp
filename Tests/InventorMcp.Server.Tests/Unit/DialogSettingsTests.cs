using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	<c>DialogSettings.jsonc</c> sets the button for each dialog type, a user file replaces single entries, and anything
/// 	the server cannot read makes the dialog "Ask".
/// </summary>
[Trait("Level", "Unit")]
public sealed class DialogSettingsTests
{
	private static readonly string _defaultText = DefaultText();

	[Fact]
	public void EmbeddedDefaultsHaveNoProblems() =>
		Assert.Empty(DialogSettings.Defaults.Problems);

	[Fact]
	public void EmbeddedDefaultsNameEveryDialogType()
	{
		HashSet<string> named = [.. System.Text.Json.JsonDocument.Parse(
			_defaultText,
			new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })
			.RootElement.EnumerateObject().Select(entry => entry.Name)];

		Assert.Equal([.. DialogPolicy.Choices.Keys.Order()], [.. named.Order()]);
	}

	[Theory]
	[InlineData("iLogicError", "OK")]
	[InlineData("iLogicCompileError", "OK")]
	[InlineData("messageBox", "OK")]
	[InlineData("dotNetDisposedObjectError", "Continue")]
	[InlineData("migration", null)]
	[InlineData("iLogicSecurityAlert", "Run the rule")]
	[InlineData("iLogicSecurityAdvisor", "OK")]
	public void EmbeddedDefaults(string type, string? button) =>
		Assert.Equal(button, DialogSettings.Defaults.ButtonFor(type));

	[Fact]
	public void UserFileReplacesOnlyItsEntries()
	{
		DialogSettings settings = Create("""{ "iLogicSecurityAlert": "Ask", "migration": "OK" }""");

		Assert.Null(settings.ButtonFor("iLogicSecurityAlert"));
		Assert.Equal("OK", settings.ButtonFor("migration"));
		Assert.Equal("OK", settings.ButtonFor("iLogicError"));
		Assert.Empty(settings.Problems);
	}

	[Fact]
	public void UserFileMayHaveComments() =>
		Assert.Null(Create("""
			// Ask me every time.
			{ "iLogicSecurityAlert": "Ask", }
			""").ButtonFor("iLogicSecurityAlert"));

	[Fact]
	public void ValueIsMatchedWithoutCase() =>
		Assert.Equal("Don't run the rule", Create("""{ "iLogicSecurityAlert": "don't RUN the rule" }""").ButtonFor("iLogicSecurityAlert"));

	[Fact]
	public void WrongValueIsAskAndReported()
	{
		DialogSettings settings = Create("""{ "iLogicError": "Cancel" }""");

		Assert.Null(settings.ButtonFor("iLogicError"));
		Assert.Contains(settings.Problems, problem => problem.Contains("'iLogicError' cannot be 'Cancel'", StringComparison.Ordinal));
	}

	[Fact]
	public void UnknownTypeIsReported() =>
		Assert.Contains(Create("""{ "saveChanges": "Yes" }""").Problems, problem => problem.Contains("'saveChanges' is not a dialog type", StringComparison.Ordinal));

	[Fact]
	public void UserFileThatIsNotJsonMakesEveryTypeAsk()
	{
		DialogSettings settings = Create("""{ "iLogicError": "OK" """);

		Assert.All(DialogPolicy.Choices.Keys, type => Assert.Null(settings.ButtonFor(type)));
		Assert.NotEmpty(settings.Problems);
	}

	[Fact]
	public void AutoCloseOffMakesEveryTypeAsk()
	{
		DialogSettings settings = DialogSettings.Create(_defaultText, userText: null, autoCloseValue: " FALSE ", acceptMigrationValue: "true");

		Assert.All(DialogPolicy.Choices.Keys, type => Assert.Null(settings.ButtonFor(type)));
	}

	[Fact]
	public void AcceptMigrationSetsMigrationToOk() =>
		Assert.Equal("OK", DialogSettings.Create(_defaultText, userText: null, autoCloseValue: null, acceptMigrationValue: "true").ButtonFor("migration"));

	private static DialogSettings Create(string userText) =>
		DialogSettings.Create(_defaultText, userText, autoCloseValue: null, acceptMigrationValue: null);

	private static string DefaultText()
	{
		using Stream stream = typeof(DialogSettings).Assembly.GetManifestResourceStream("InventorMcp.Server.DialogSettings.jsonc")!;
		using StreamReader reader = new(stream);

		return reader.ReadToEnd();
	}
}
