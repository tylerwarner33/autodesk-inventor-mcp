using System.Text.Json;

namespace InventorMcp.Server.Services;

/// <summary>
/// 	The button that the server clicks on each dialog type of the catalog, or none to leave it for a person.
/// </summary>
/// <remarks>
/// 	The defaults are <c>DialogSettings.json</c> in the server project, embedded in the server.
/// 	A file of the same name in <c>%LOCALAPPDATA%\InventorMcp\</c> replaces single entries, so a user of a packaged
/// 	server can change them.
/// 	A file that cannot be read makes every type "Ask", because a wrong click can lose data and a wrong "Ask" only stops
/// 	a call.
/// </remarks>
internal sealed class DialogSettings
{
	/// <summary>
	/// 	The name of the settings file, the same for the defaults and for the user's own file.
	/// </summary>
	public const string FileName = "DialogSettings.json";

	/// <summary>
	/// 	The value that leaves a dialog open for a person.
	/// </summary>
	public const string AskValue = "Ask";

	private const string _resourceName = "InventorMcp.Server.DialogSettings.json";

	private static readonly JsonDocumentOptions _jsonOptions = new()
	{
		CommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	private static readonly Lazy<string> _defaultText = new(ReadDefaultText);

	private readonly Dictionary<string, string> _buttons;

	private DialogSettings(Dictionary<string, string> buttons, IReadOnlyList<string> problems)
	{
		_buttons = buttons;
		Problems = problems;
	}

	/// <summary>
	/// 	The path of the user's own settings file, which need not exist.
	/// </summary>
	public static string UserFilePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"InventorMcp",
		FileName);

	/// <summary>
	/// 	The embedded defaults only, with no user file and no environment variable.
	/// </summary>
	public static DialogSettings Defaults => Create(_defaultText.Value, userText: null, autoCloseValue: null, acceptMigrationValue: null);

	/// <summary>
	/// 	What was wrong in the settings, ex. a value that a dialog type does not accept. Empty when all is correct.
	/// </summary>
	public IReadOnlyList<string> Problems { get; }

	/// <summary>
	/// 	Reads the embedded defaults, the user's own file and the environment variables.
	/// </summary>
	/// <remarks>
	/// 	The server calls it one time, when it starts, so a change to the user file needs a restart of the server.
	/// 	An agent that runs as the user can write the file, and the restart keeps a person in the loop before a looser
	/// 	setting has an effect.
	/// </remarks>
	public static DialogSettings Load()
	{
		string? userText = null;
		string? userProblem = null;

		try
		{
			if (File.Exists(UserFilePath))
				userText = File.ReadAllText(UserFilePath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			userProblem = $"{UserFilePath} could not be read, so every dialog is left for a person: {exception.Message}";
		}

		if (userProblem is not null)
			return new DialogSettings([], [userProblem]);

		return Create(
			_defaultText.Value,
			userText,
			Environment.GetEnvironmentVariable(DialogPolicy.AutoCloseVariable),
			Environment.GetEnvironmentVariable(DialogPolicy.AcceptMigrationVariable));
	}

	/// <summary>
	/// 	Builds the settings from the texts of the two files and the values of the two environment variables.
	/// </summary>
	/// <param name="defaultText">
	/// 	The embedded defaults.
	/// </param>
	/// <param name="userText">
	/// 	The user's own file, or null when there is none.
	/// </param>
	/// <param name="autoCloseValue">
	/// 	The value of <see cref="DialogPolicy.AutoCloseVariable"/>. <c>false</c> makes every type "Ask".
	/// </param>
	/// <param name="acceptMigrationValue">
	/// 	The value of <see cref="DialogPolicy.AcceptMigrationVariable"/>. <c>true</c> makes the migration dialog <c>OK</c>.
	/// </param>
	/// <returns>
	/// 	The settings.
	/// </returns>
	internal static DialogSettings Create(string defaultText, string? userText, string? autoCloseValue, string? acceptMigrationValue)
	{
		List<string> problems = [];
		Dictionary<string, string> buttons = new(StringComparer.Ordinal);

		if (Apply(defaultText, "the embedded DialogSettings.json", buttons, problems) is false
			|| (userText is not null && Apply(userText, UserFilePath, buttons, problems) is false))
		{
			problems.Add("So every dialog is left for a person.");
			return new DialogSettings([], problems);
		}

		if (IsValue(acceptMigrationValue, "true"))
			buttons[DialogPolicy.MigrationType] = DialogPolicy.OkButton;

		if (IsValue(autoCloseValue, "false"))
			buttons.Clear();

		return new DialogSettings(buttons, problems);
	}

	/// <summary>
	/// 	The button to click on a dialog of the type, or null to leave it for a person.
	/// </summary>
	/// <param name="type">
	/// 	The catalog type, from <see cref="DialogPolicy.Classify"/>.
	/// </param>
	public string? ButtonFor(string type) => _buttons.TryGetValue(type, out string? button) ? button : null;

	/// <summary>
	/// 	Copies each entry of a settings text into the buttons.
	/// </summary>
	/// <returns>
	/// 	False when the text is not a JSON object, so none of its entries can be trusted.
	/// </returns>
	private static bool Apply(string text, string source, Dictionary<string, string> buttons, List<string> problems)
	{
		JsonDocument document;

		try
		{
			document = JsonDocument.Parse(text, _jsonOptions);
		}
		catch (JsonException exception)
		{
			problems.Add($"{source} is not valid JSON: {exception.Message}");
			return false;
		}

		using (document)
		{
			if (document.RootElement.ValueKind is not JsonValueKind.Object)
			{
				problems.Add($"{source} must hold one JSON object.");
				return false;
			}

			foreach (JsonProperty entry in document.RootElement.EnumerateObject())
				ApplyEntry(entry, source, buttons, problems);
		}

		return true;
	}

	private static void ApplyEntry(JsonProperty entry, string source, Dictionary<string, string> buttons, List<string> problems)
	{
		if (DialogPolicy.Choices.TryGetValue(entry.Name, out IReadOnlyList<string>? choices) is false)
		{
			problems.Add($"{source}: '{entry.Name}' is not a dialog type. The types are: {string.Join(", ", DialogPolicy.Choices.Keys)}.");
			return;
		}

		string? value = entry.Value.ValueKind is JsonValueKind.String ? entry.Value.GetString()?.Trim() : null;

		// A wrong value must not keep an earlier click, so it removes the entry.
		_ = buttons.Remove(entry.Name);

		if (string.Equals(value, AskValue, StringComparison.OrdinalIgnoreCase))
			return;

		string? button = choices.FirstOrDefault(choice => string.Equals(choice, value, StringComparison.OrdinalIgnoreCase));

		if (button is null)
		{
			problems.Add($"{source}: '{entry.Name}' cannot be '{entry.Value}', so it is \"Ask\". The values are: {string.Join(", ", choices.Append(AskValue).Select(choice => $"\"{choice}\""))}.");
			return;
		}

		buttons[entry.Name] = button;
	}

	private static bool IsValue(string? value, string expected) =>
		string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

	private static string ReadDefaultText()
	{
		using Stream stream = typeof(DialogSettings).Assembly.GetManifestResourceStream(_resourceName)
			?? throw new InvalidOperationException($"The embedded resource {FileName} is missing.");

		using StreamReader reader = new(stream);

		return reader.ReadToEnd();
	}
}
